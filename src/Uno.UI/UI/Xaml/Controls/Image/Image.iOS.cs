using System;
using System.Threading;
using System.Threading.Tasks;
using CoreGraphics;
using UIKit;
using Uno.Diagnostics.Eventing;
using Uno.Extensions;
using Uno.Logging;
using Uno.UI.Extensions;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using CoreAnimation;
using Uno.UI;

namespace Windows.UI.Xaml.Controls
{
	public partial class Image
	{
		private Size _sourceImageSize;

		/// <summary>
		/// The size of the native image data
		/// </summary>
		private Size SourceImageSize
		{
			get => _sourceImageSize;
			set
			{
				_sourceImageSize = value;

				if (Source is BitmapSource bitmapSource)
				{
					bitmapSource.PixelWidth = (int)_sourceImageSize.Width;
					bitmapSource.PixelHeight = (int)_sourceImageSize.Height;
				}
			}
		}

		public Image()
		{
			UserInteractionEnabled = true;
		}

		private void TryOpenImage()
		{
			//Skip opening the image source source is already loaded or if the view isn't loaded
			if (_openedImage == Source)
			{
				if (this.Log().IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
				{
					this.Log().Debug(this.ToString() + " TryOpenImage - cancelling because Source has not changed");
				}
				return;
			}

			if (Window == null)
			{
				if (this.Log().IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
				{
					this.Log().Debug(this.ToString() + " TryOpenImage - cancelling because view is not loaded");
				}
				return;
			}

			if (this.Log().IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
			{
				this.Log().Debug(this.ToString() + " TryOpenImage - proceeding");
			}

			using (
				_imageTrace.WriteEventActivity(
					TraceProvider.Image_SetSourceStart,
					TraceProvider.Image_SetSourceStop,
					new object[] { this.GetDependencyObjectId() }
				)
			)
			{
				_openedImage = Source;

				if (_openedImage == null || !_openedImage.HasSource())
				{
					_native?.Reset();
					SetNeedsLayoutOrDisplay();
					_imageFetchDisposable.Disposable = null;
				}
				else if (_openedImage.TryOpenSync(out var img))
				{
					SetImage(img);
					_imageFetchDisposable.Disposable = null;
				}
				else
				{
					// The Jupiter behavior is to reset the visual right away, displaying nothing
					// then show the new image. We're rescheduling the work below, so there is going
					// to be a visual blank displayed.
					TryCreateNative();
					_native.Reset();

					Func<CancellationToken, Task> scheduledFetch = async (ct) =>
					{
						using (
						   _imageTrace.WriteEventActivity(
							   TraceProvider.Image_SetUriStart,
							   TraceProvider.Image_SetUriStop,
							   new object[] { this.GetDependencyObjectId() }
						   )
						)
						{
							//_openedImage could be set to null while trying to access it on the thread pool
							var image = await Task.Run(() => _openedImage?.Open(ct));

							//if both image and _openedImage are null this is ok just return;
							//otherwise call SetImage with null which will raise the OnImageFailed event
							if (ct.IsCancellationRequested ||
								(image == null && _openedImage == null))
							{
								return;
							}

							SetImage(image);
						}
					};

					Execute(scheduledFetch);
				}
			}
		}

		private void SetImage(UIImage image)
		{
			using (
				_imageTrace.WriteEventActivity(
					TraceProvider.Image_SetImageStart,
					TraceProvider.Image_SetImageStop,
					new object[] { this.GetDependencyObjectId() }
				)
			)
			{
				if (MonochromeColor != null)
				{
					image = image.AsMonochrome(MonochromeColor.Value);
				}

				TryCreateNative();

				_native.SetImage(image);

				SourceImageSize = image?.Size.ToFoundationSize() ?? default(Size);
			}

			SetNeedsLayout();

			if (_native.HasImage)
			{
				OnImageOpened(image);
			}
			else
			{
				OnImageFailed(image);
			}
		}

		private void TryCreateNative()
		{
			if (_native == null)
			{
				_native = new NativeImage();

				Add(_native);

				UpdateContentMode(Stretch);
			}
		}

		private void SetNeedsLayoutOrDisplay()
		{
			if (ShouldDowngradeLayoutRequest())
			{
				SetNeedsDisplay();
			}
			else
			{
				SetNeedsLayout();
			}
		}

		private void UpdateContentMode(Stretch stretch)
		{
			if (FeatureConfiguration.Image.LegacyIosAlignment && _native != null)
			{
				switch (stretch)
				{
					case Stretch.Uniform:
						_native.ContentMode = UIViewContentMode.ScaleAspectFit;
						break;

					case Stretch.None:
						_native.ContentMode = UIViewContentMode.Center;
						break;

					case Stretch.UniformToFill:
						_native.ContentMode = UIViewContentMode.ScaleAspectFill;
						break;

					case Stretch.Fill:
						_native.ContentMode = UIViewContentMode.ScaleToFill;
						break;

					default:
						throw new NotSupportedException(
							"Stretch mode {0} is not supported".InvariantCultureFormat(stretch));
				}
			}
			else
			{
				SetNeedsLayout();
			}
		}

		public override void LayoutSubviews()
		{
			try
			{
				base.LayoutSubviews();

				UpdateLayerRect();
			}
			catch (Exception e)
			{
				this.Log().Error($"Layout failed in {GetType()}", e);
			}
		}

		partial void OnStretchChanged(Stretch newValue, Stretch oldValue)
		{
			UpdateContentMode(newValue);
		}

		private void UpdateLayerRect()
		{
			// Use "Bounds" over "Frame" because it includes all transforms
			var availableSize = Bounds.Size.ToFoundationSize(); ;

			if (SourceImageSize.Width == 0 || SourceImageSize.Height == 0 || availableSize.Width == 0 || availableSize.Height == 0 || (!_native?.HasImage ?? true))
			{
				return; // nothing to do
			}

			if (FeatureConfiguration.Image.LegacyIosAlignment)
			{
				return;
			}

			var imageSize = _native.ImageSize.ToFoundationSize();

			// Calculate the resulting space required on screen for the image
			var containerSize = this.MeasureSource(availableSize, imageSize);

			// Calculate the position of the image to follow stretch and alignment requirements
			var contentRect = this.ArrangeSource(availableSize, containerSize);

			// Calculate the required container to position the image in the AvailableSize
			var containerRect = new Rect(default, availableSize);
			containerRect.Intersect(contentRect);

			// Calculate a relative (0 to 1) X, Y, Width & Height for the image position
			var relativeX = contentRect.X / contentRect.Width;
			var relativeY = contentRect.Y / contentRect.Height;
			var relativeWidth = availableSize.Width / contentRect.Width;
			var relativeHeight = availableSize.Height / contentRect.Height;
			var contentRelativeRect = new CGRect(-relativeX, -relativeY, relativeWidth, relativeHeight);

			// Apply the relative position
			_native.Layer.ContentsRect = contentRelativeRect;

			// Add a clipping mask to prevent the GPU from rendering padding pixels
			_native.Layer.Mask = new CAShapeLayer
			{
				Path = CGPath.FromRect(containerRect.ToCGRect())
			};
		}
	}
}

