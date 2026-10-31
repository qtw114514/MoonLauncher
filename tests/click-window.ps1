param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][int]$X,
    [Parameter(Mandatory=$true)][int]$Y,
    [int]$DelayMilliseconds = 500
)
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ClickWin {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
[ClickWin]::SetProcessDPIAware() | Out-Null
$p = Get-Process -Id $ProcessId -ErrorAction Stop
$p.Refresh()
$h = $p.MainWindowHandle
if ($h -eq [IntPtr]::Zero) { throw 'No main window.' }
$r = New-Object ClickWin+RECT
[ClickWin]::GetWindowRect($h, [ref]$r) | Out-Null
[ClickWin]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 200
[ClickWin]::SetCursorPos($r.Left + $X, $r.Top + $Y) | Out-Null
Start-Sleep -Milliseconds 100
[ClickWin]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero)
[ClickWin]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds $DelayMilliseconds
Write-Output "Clicked $X,$Y in process $ProcessId"
