// Default URL for triggering event grid function in the local environment.
// http://localhost:7071/runtime/webhooks/EventGrid?functionName={functionname}

// Learn how to locally debug an Event Grid-triggered function:
//    https://aka.ms/AA30pjh

// Use for local testing:
//   https://{ID}.ngrok.io/runtime/webhooks/EventGrid?functionName=Thumbnail

using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Azure.EventGrid.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.EventGrid;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WebPWrapper.Encoder;
using Image = SixLabors.ImageSharp.Image;

namespace ImageFunctions
{
    public static class Thumbnail
    {
        private static readonly string BLOB_STORAGE_CONNECTION_STRING = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        private static readonly string THUMBNAIL_POSTFIX_FILENAME = "-thumbnail-";
        private static readonly string SUPPORTED_IMAGE_EXTENSIONS = "gif|png|jpe?g";
        private static readonly bool IS_WEBP = Convert.ToBoolean(Environment.GetEnvironmentVariable("WEBP_SUPPORT"));
        private static readonly string WEBP_EXTENSION  = ".webp";
        private static readonly string WEBP_CONVERTER_PATH = "modules/bin/cwebp.exe";


        private static string GetBlobNameFromUrl(string bloblUrl)
        {
            var uri = new Uri(bloblUrl);
            var blobClient = new BlobClient(uri);
            return blobClient.Name;
        }

        private static IImageEncoder GetEncoder(string extension)
        {
            IImageEncoder encoder = null;

            extension = extension.Replace(".", "");

            var isSupported = Regex.IsMatch(extension, SUPPORTED_IMAGE_EXTENSIONS, RegexOptions.IgnoreCase);

            if (isSupported)
            {
                switch (extension.ToLower())
                {
                    case "png":
                        encoder = new PngEncoder();
                        break;
                    case "jpg":
                        encoder = new JpegEncoder();
                        break;
                    case "jpeg":
                        encoder = new JpegEncoder();
                        break;
                    case "gif":
                        encoder = new GifEncoder();
                        break;
                    default:
                        break;
                }
            }

            return encoder;
        }

        private static Stream GetWebp(int size, MemoryStream input, ILogger log)
        {
            var output = new MemoryStream();
            var path = Path.GetFullPath(WEBP_CONVERTER_PATH);
            log.LogInformation($"Starting processing using webp converter path ${path} for thumbnail size {size}");
            var builder = new WebPEncoderBuilder(path);
            var encoder = builder
                .Resize(size, 0)
                .AlphaConfig(x => x
                    .TransparentProcess(
                        TransparentProcesses.Blend,
                        System.Drawing.Color.Yellow
                    )
                ).CompressionConfig(x => x.Lossy(y => y.Quality(90))).Build();
            encoder.Encode(input, output);
            output.Position = 0;

            log.LogInformation("Completed processing webp");
            return output;
        }

        [FunctionName("Thumbnail")]
        public static Task Run(
            [EventGridTrigger]EventGridEvent eventGridEvent,
            [Blob("{data.url}", FileAccess.Read)] Stream input,
            ILogger log)
        {
            try
            {
                log.LogInformation("Starting Thumbnail function...");

                if (input != null)
                {
                    var createdEvent = ((JObject)eventGridEvent.Data).ToObject<StorageBlobCreatedEventData>();
                    log.LogInformation($"Event url {createdEvent.Url}");
                    var originExtension = Path.GetExtension(createdEvent.Url);
                    var extension = IS_WEBP ? WEBP_EXTENSION : originExtension;
                    log.LogInformation($"Target extension {extension}");
                    var encoder = GetEncoder(originExtension);

                    if (encoder != null)
                    {
                        var thumbnailWidths = Environment.GetEnvironmentVariable("THUMBNAIL_WIDTHS").Trim().Split(',').Select((width) => Convert.ToInt32(width)).ToList();
                        var thumbContainerName = Environment.GetEnvironmentVariable("THUMBNAIL_CONTAINER_NAME");
                        var blobServiceClient = new BlobServiceClient(BLOB_STORAGE_CONNECTION_STRING);
                        var blobContainerClient = blobServiceClient.GetBlobContainerClient(thumbContainerName);
                        var originBlobName = GetBlobNameFromUrl(createdEvent.Url);
                        var blobHttpHeader = new BlobHttpHeaders { ContentType = "image/" + extension.Replace(".", "") };

                        using (Image<Rgba32> originImage = Image.Load<Rgba32>(input))
                        {
                            thumbnailWidths.ForEach(async (width) =>
                            {
                                var image = originImage.CloneAs<Rgba32>();
                                var blobName = originBlobName.Replace(originExtension, $"{THUMBNAIL_POSTFIX_FILENAME}{width}{extension}");

                                using (var output = new MemoryStream())
                                {
                                    
                                    if (!IS_WEBP && width <= image.Width)
                                    {
                                        var height = Convert.ToInt32(Math.Round((decimal)((width * image.Height) / image.Width)));

                                        image.Mutate(x => x.Resize(width, height));
                                    }

                                    image.Save(output, encoder);
                                    output.Position = 0;

                                    var stream = IS_WEBP ? GetWebp(width, output, log) : output;

                                    await blobContainerClient.GetBlobClient(blobName).UploadAsync(stream, new BlobUploadOptions { HttpHeaders = blobHttpHeader });

                                    await output.DisposeAsync();

                                    if (IS_WEBP)
                                    {
                                        await stream.DisposeAsync();
                                    }
                                }
                            });
                        }
                    }
                    else
                    {
                        log.LogInformation($"No encoder support for: {createdEvent.Url}");
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogInformation(ex.Message);
                throw;
            }

            return Task.CompletedTask;
        }
    }
}
