using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SonicRoute
{
    /// <summary>Clipboard contention must never block the window dispatcher.</summary>
    internal static class ClipboardWriter
    {
        internal static Task<bool> TryWriteTextAsync(string text)
            => TryWriteTextAsync(text, value =>
            {
                var data = new System.Windows.Forms.DataObject();
                data.SetText(value, System.Windows.Forms.TextDataFormat.UnicodeText);
                System.Windows.Forms.Clipboard.SetDataObject(data, true, 0, 0);
            });

        internal static Task<bool> TryWriteTextAsync(string text, Action<string> write)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = new Thread(() =>
            {
                try
                {
                    for (int attempt = 0; attempt < 10; attempt++)
                    {
                        try
                        {
                            // Disable the framework's synchronous retry loop. All waiting happens
                            // on this short-lived STA thread, with the UI free to process messages.
                            write(text);
                            completion.TrySetResult(true);
                            return;
                        }
                        catch (ExternalException) when (attempt < 9)
                        {
                            Thread.Sleep(75);
                        }
                    }
                }
                catch (Exception)
                {
                    completion.TrySetResult(false);
                }
            }) { IsBackground = true, Name = "SonicRoute clipboard copy" };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            return completion.Task;
        }
    }
}
