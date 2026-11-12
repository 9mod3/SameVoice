using System.IO;
using System.Runtime.InteropServices;

namespace SameVoice.App.Audio;

/// <summary>
/// Minimal Voicemeeter Remote API client used for muting only the A1 bus.
/// In the dual-route setup A1 is the local physical output and B1 is the stream
/// SameVoice captures, so an A1 bus mute must not interrupt Android playback.
/// </summary>
internal static class VoicemeeterRemote
{
    private const string A1MuteParameter = "Bus[0].Mute";
    private static readonly object Gate = new();

    private static IntPtr _library;
    private static LoginDelegate? _login;
    private static LogoutDelegate? _logout;
    private static SetParameterFloatDelegate? _setParameterFloat;
    private static GetParameterFloatDelegate? _getParameterFloat;
    private static bool _loggedIn;

    public static bool TryGetA1Mute(out bool muted)
    {
        muted = false;
        lock (Gate)
        {
            if (!EnsureInitialized()) return false;
            int hr = _getParameterFloat!(A1MuteParameter, out float value);
            if (hr != 0) return false;
            muted = value >= 0.5f;
            return true;
        }
    }

    public static bool TrySetA1Mute(bool muted)
    {
        lock (Gate)
        {
            if (!EnsureInitialized()) return false;
            return _setParameterFloat!(A1MuteParameter, muted ? 1f : 0f) == 0;
        }
    }

    private static bool EnsureInitialized()
    {
        if (_loggedIn) return true;
        foreach (string path in CandidatePaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                IntPtr library = NativeLibrary.Load(path);
                var login = GetDelegate<LoginDelegate>(library, "VBVMR_Login");
                var logout = GetDelegate<LogoutDelegate>(library, "VBVMR_Logout");
                var set = GetDelegate<SetParameterFloatDelegate>(library, "VBVMR_SetParameterFloat");
                var get = GetDelegate<GetParameterFloatDelegate>(library, "VBVMR_GetParameterFloat");
                if (login is null || logout is null || set is null || get is null)
                {
                    NativeLibrary.Free(library);
                    continue;
                }

                // 0 = success, 1 = already logged in, < 0 = failure.
                if (login() < 0)
                {
                    NativeLibrary.Free(library);
                    continue;
                }

                _library = library;
                _login = login;
                _logout = logout;
                _setParameterFloat = set;
                _getParameterFloat = get;
                _loggedIn = true;
                return true;
            }
            catch
            {
                // Voicemeeter not installed, wrong bitness, or API not running.
            }
        }
        return false;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (string root in new[]
                 {
                     Path.Combine(programFilesX86, "VB", "Voicemeeter"),
                     Path.Combine(programFiles, "VB", "Voicemeeter"),
                     Path.Combine(programFilesX86, "VB", "Voicemeeter Banana"),
                     Path.Combine(programFiles, "VB", "Voicemeeter Banana")
                 })
        {
            yield return Path.Combine(root, "VoicemeeterRemote64.dll");
            yield return Path.Combine(root, "VoicemeeterRemote.dll");
        }
        yield return Path.Combine(AppContext.BaseDirectory, "VoicemeeterRemote64.dll");
    }

    private static T? GetDelegate<T>(IntPtr library, string export) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(library, export, out IntPtr address) || address == IntPtr.Zero)
            return null;
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LoginDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LogoutDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int SetParameterFloatDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string parameter, float value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int GetParameterFloatDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string parameter, out float value);
}
