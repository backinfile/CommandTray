using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommandTray.Core;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private Task OpenQrInputAsync(string text)
    {
        _qrInput = text;
        Navigate("qr");
        return Task.CompletedTask;
    }

    private async Task ChooseQrImageAsync(TextBox generator, CancellationToken token)
    {
        var chosen = await RequireTopLevel().StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L("选择二维码图片", "Choose a QR image"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(L("图片", "Images"))
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"],
                    MimeTypes = ["image/png", "image/jpeg", "image/webp", "image/bmp"]
                }
            ]
        });
        if (chosen.Count == 0) return;
        using var image = chosen[0];
        token.ThrowIfCancellationRequested();
        await RecognizeQrImageAsync(image.OpenReadAsync, token, text =>
        {
            generator.Text = text;
            return Task.CompletedTask;
        });
    }

    private async Task RecognizeQrImageAsync(Func<Task<Stream>> open, CancellationToken token,
        Func<string, Task> useInGenerator)
    {
        Report(L("正在识别二维码…", "Reading QR code…"));
        string? text;
        try
        {
            await using var image = await open();
            text = await QrImageReader.ReadAsync(image, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (InvalidDataException error) when (!token.IsCancellationRequested)
        {
            Report(error.Message == "image-limit"
                ? L("图片超过 32 MiB 或 2,400 万像素。", "The image exceeds 32 MiB or 24 megapixels.")
                : L("无法读取图片。", "The image could not be read."));
            return;
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is IOException or UnauthorizedAccessException)
        {
            Report(L("无法读取图片。", "The image could not be read."));
            return;
        }
        if (token.IsCancellationRequested) return;
        if (string.IsNullOrEmpty(text))
        {
            Report(L("图片中未找到二维码。", "No QR code found in the image."));
            return;
        }

        Report("");
        var result = UtilityInput(L("识别结果", "Decoded text"), text, multiline: true, readOnly: true);
        var close = CloseActionButton(L("关闭", "Close"));
        var actions = ActionBar(close, UtilityCopy(L("复制结果", "Copy result"), () => text, token));
        if (CanGenerateQr(text))
        {
            actions.Children.Add(Action(L("用作生成内容", "Use for generation"), async () =>
            {
                CloseActionDialog();
                await useInGenerator(text);
            }));
        }
        OpenActionDialog(L("二维码识别结果", "QR scan result"), result, actions, result);
    }

    private static bool CanGenerateQr(string text) =>
        text.Length is > 0 and <= LocalUtilities.QrByteLimit && System.Text.Encoding.UTF8.GetByteCount(text) <= LocalUtilities.QrByteLimit;
}
