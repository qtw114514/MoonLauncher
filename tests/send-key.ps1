param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][string]$Keys,
    [int]$DelayMilliseconds = 500
)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class KeyWin { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd); }'
$p = Get-Process -Id $ProcessId -ErrorAction Stop
$p.Refresh()
[KeyWin]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 250
[System.Windows.Forms.SendKeys]::SendWait($Keys)
Start-Sleep -Milliseconds $DelayMilliseconds
Write-Output "Sent $Keys to process $ProcessId"
