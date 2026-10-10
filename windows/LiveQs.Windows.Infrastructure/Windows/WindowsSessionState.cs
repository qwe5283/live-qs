using System.Diagnostics;
using System.Runtime.InteropServices;
using LiveQs.Windows.Core.Abstractions;

namespace LiveQs.Windows.Infrastructure.Windows;

public sealed class WindowsSessionState : ISessionStateProvider
{
    private readonly int _sessionId = Process.GetCurrentProcess().SessionId;
    private int _locked;

    public bool IsLocked
    {
        get
        {
            if (!NativeMethods.WTSQuerySessionInformation(nint.Zero, _sessionId, 25, out var buffer, out var length))
            {
                return Volatile.Read(ref _locked) != 0;
            }
            try
            {
                // WTSINFOEX level 1: the aligned data starts at offset 8; SessionFlags is its third DWORD.
                if (length >= 20 && Marshal.ReadInt32(buffer) == 1)
                {
                    var flags = Marshal.ReadInt32(buffer, 16);
                    if (flags is 0 or 1)
                    {
                        SetLocked(flags == 0);
                    }
                }
                return Volatile.Read(ref _locked) != 0;
            }
            finally
            {
                NativeMethods.WTSFreeMemory(buffer);
            }
        }
    }

    public void SetLocked(bool locked) => Volatile.Write(ref _locked, locked ? 1 : 0);
}
