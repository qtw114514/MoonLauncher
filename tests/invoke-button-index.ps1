param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][int]$Index,
    [int]$DelayMilliseconds = 500
)
Add-Type -AssemblyName UIAutomationClient
$p = Get-Process -Id $ProcessId -ErrorAction Stop
$p.Refresh()
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$elements = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
if ($Index -ge $elements.Count) { throw "Button index out of range: $Index / $($elements.Count)" }
$element = $elements[$Index]
$pattern = $null
if (-not $element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { throw 'InvokePattern unavailable.' }
$pattern.Invoke()
Start-Sleep -Milliseconds $DelayMilliseconds
Write-Output "Invoked button index $Index"
