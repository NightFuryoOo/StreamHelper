using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

internal static class ClipboardText
{
    private const int Attempts = 3;

    public static bool TrySet(string text)
    {
        if (Environment.GetEnvironmentVariable("STREAMHELPER_NO_CLIPBOARD") == "1")
        {
            Log.Write("Clipboard not touched (test mode): " + text.Replace(Environment.NewLine, " ⏎ "));
            return true;
        }
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException) when (attempt < Attempts)
            {
                Thread.Sleep(60);
            }
            catch (ExternalException ex)
            {
                Log.Write("Copy to clipboard failed: " + ex.Message);
                return false;
            }
        }
    }
}
