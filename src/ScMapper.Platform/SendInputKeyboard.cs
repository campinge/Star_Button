using System.Runtime.InteropServices;
using ScMapper.Core.Input;

namespace ScMapper.Platform;

/// <summary>Keyboard output via SendInput with hardware scan codes (what games read).</summary>
public sealed class SendInputKeyboard : IKeyOutput
{
    private const uint InputKeyboard = 1;
    private const uint KeyeventfExtendedkey = 0x1, KeyeventfKeyup = 0x2, KeyeventfScancode = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KeybdInput ki;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, Input[] inputs, int size);

    public void Down(IReadOnlyList<KeyStroke> keys) => Send(keys, up: false);
    public void Up(IReadOnlyList<KeyStroke> keys) => Send(keys.Reverse().ToList(), up: true);

    private static void Send(IReadOnlyList<KeyStroke> keys, bool up)
    {
        var inputs = new Input[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            uint flags = KeyeventfScancode;
            if (keys[i].Extended) flags |= KeyeventfExtendedkey;
            if (up) flags |= KeyeventfKeyup;
            inputs[i] = new Input { type = InputKeyboard, ki = new KeybdInput { wScan = keys[i].Scan, dwFlags = flags } };
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }
}
