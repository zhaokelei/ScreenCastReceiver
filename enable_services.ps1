param([int]$ProcessId = 4828)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCond)
if (-not $win) { Write-Host "NO_WINDOW"; exit 1 }

$cbCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::CheckBox)
$cbs = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cbCond)
Write-Host "CHECKBOX_COUNT=$($cbs.Count)"

for ($i = 0; $i -lt $cbs.Count; $i++) {
    $tp = $cbs[$i].GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($tp.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
        $tp.Toggle()
        Write-Host "TOGGLED_INDEX=$i"
    } else {
        Write-Host "ALREADY_ON_INDEX=$i"
    }
}
Start-Sleep -Seconds 8
Write-Host "DONE"
