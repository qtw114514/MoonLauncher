param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][string]$Base64Text,
    [int]$Index = 0,
    [string]$AutomationId = ''
)
Add-Type -AssemblyName UIAutomationClient
$text = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($Base64Text))
$p = Get-Process -Id $ProcessId -ErrorAction Stop
$p.Refresh()
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
if ($AutomationId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
else {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
    $elements = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($Index -ge $elements.Count) { throw "Edit index out of range: $Index / $($elements.Count)" }
    $element = $elements[$Index]
}
if (-not $element) { throw 'Edit control not found.' }
$pattern = $null
if (-not $element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { throw 'ValuePattern is unavailable.' }
$pattern.SetValue($text)
Write-Output "Set edit value to $text"
