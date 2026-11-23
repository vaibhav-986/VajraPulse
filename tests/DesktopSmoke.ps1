param([ValidateSet('Inspect','Expand','Exercise','Capture','Collapse')][string]$Mode = 'Inspect')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PulseTestNative {
 [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
}
'@
[void][PulseTestNative]::SetProcessDpiAwarenessContext([IntPtr](-4))
$p = Get-Process VajraPulse | Select-Object -First 1
$condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$p.Id)
$root = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
if ($null -eq $root) { throw 'Vajra Pulse has no desktop window.' }
 function Get-Control([string]$name) {
  for ($i = 0; $i -lt 5; $i++) {
   $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
   $found = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
   if ($null -ne $found) { return $found }
   Start-Sleep -Milliseconds 500
  }
  return $null
 }
 function Invoke-Control([string]$name) {
  $c = Get-Control $name
  if ($null -eq $c) { throw "Control not found: $name" }
  $pattern = $c.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
  $pattern.Invoke()
 }
function Assert-Control([string]$name) {
 if ($null -eq (Get-Control $name)) { throw "Expected control missing: $name" }
 "PASS: $name"
}
function Assert-ControlEnabled([string]$name) {
 $c = Get-Control $name
 if ($null -eq $c -or -not $c.Current.IsEnabled) { throw "Expected enabled control missing: $name" }
 "PASS: $name is enabled"
}
function Save-Capture([string]$name) {
 $r = $root.Current.BoundingRectangle
 $bitmap = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
 $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
 try {
  $graphics.CopyFromScreen([int]$r.Left, [int]$r.Top, 0, 0, $bitmap.Size)
  $bitmap.Save((Join-Path $PSScriptRoot $name), [System.Drawing.Imaging.ImageFormat]::Png)
 } finally { $graphics.Dispose(); $bitmap.Dispose() }
 "Captured $name"
}
if ($Mode -eq 'Inspect') {
 $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object {
  [pscustomobject]@{Name=$_.Current.Name;Type=$_.Current.ControlType.ProgrammaticName;Enabled=$_.Current.IsEnabled;Offscreen=$_.Current.IsOffscreen}
 }
 return
}
 if ($Mode -eq 'Expand' -or $Mode -eq 'Exercise') {
  if ($null -ne (Get-Control 'Expand Vajra Pulse')) { Invoke-Control 'Expand Vajra Pulse' }
  Start-Sleep -Milliseconds 1000
  Assert-Control 'Show focus timer'
 }
 if ($Mode -eq 'Exercise') {
  Assert-Control 'Show tools'
  Invoke-Control 'Show tools'
  Start-Sleep -Milliseconds 1000
  Assert-Control 'Show system activity'
  Invoke-Control 'Show system activity'
  Start-Sleep -Milliseconds 1000
  Assert-Control 'Show application shortcuts'
  Assert-Control 'Show application volume mixer'
  Assert-Control 'CPU usage'
  Assert-Control 'Memory usage'
  Save-Capture 'tools-system.png'
  Invoke-Control 'Show application shortcuts'
  Start-Sleep -Milliseconds 1000
  1..5 | ForEach-Object { Assert-ControlEnabled "Application slot $_" }
  Save-Capture 'tools-apps.png'
  Invoke-Control 'Show application volume mixer'
  Start-Sleep -Milliseconds 1000
  Save-Capture 'tools-mixer.png'
  Invoke-Control 'Show music controls'
  Start-Sleep -Milliseconds 1000
  Save-Capture 'expanded.png'
  Invoke-Control 'Show focus timer'
  Start-Sleep -Milliseconds 1000
  Assert-Control 'Start focus'
 Save-Capture 'focus.png'
 Invoke-Control 'Start focus'
 Assert-Control 'Pause'
 Invoke-Control 'Collapse Vajra Pulse'
 Start-Sleep -Milliseconds 650
 Save-Capture 'activities.png'
 if ($null -ne (Get-Control 'Open secondary music activity')) {
  Assert-Control 'Open secondary music activity'
  Invoke-Control 'Open secondary music activity'
  Start-Sleep -Milliseconds 650
  Assert-Control 'Play or pause'
  Save-Capture 'secondary.png'
  Invoke-Control 'Show focus timer'
  Start-Sleep -Milliseconds 220
 } else {
  Invoke-Control 'Expand Vajra Pulse'
  Start-Sleep -Milliseconds 650
 }
 Assert-Control 'Pause'
 Invoke-Control 'Pause'
 Assert-Control 'Resume'
 Invoke-Control 'Resume'
 Assert-Control 'Pause'
 Invoke-Control 'Reset focus timer'
 Assert-Control 'Start focus'
 Invoke-Control 'Open settings'
 Start-Sleep -Milliseconds 650
 Assert-Control 'Make it yours.'
 Assert-Control 'Expand on hover'
 Assert-Control 'Show System side button'
 Assert-Control 'Show Bluetooth side button'
 Assert-Control 'Show Wi-Fi side button'
 1..5 | ForEach-Object { Assert-Control "Choose or replace application slot $_" }
 Save-Capture 'settings.png'
 Invoke-Control 'Back to island'
 Start-Sleep -Milliseconds 650
 Assert-Control 'Start focus'
 Invoke-Control 'Show music controls'
 Start-Sleep -Milliseconds 220
 Invoke-Control 'Collapse Vajra Pulse'
 Start-Sleep -Milliseconds 650
 Assert-Control 'Expand Vajra Pulse'
 Save-Capture 'compact.png'
 'PASS: Music/focus activity switching, timer controls, compact live activities, settings, and collapse.'
}
if ($Mode -eq 'Collapse') {
 if ($null -ne (Get-Control 'Collapse Vajra Pulse')) { Invoke-Control 'Collapse Vajra Pulse' }
 Start-Sleep -Milliseconds 650
 Assert-Control 'Expand Vajra Pulse'
}
if ($Mode -eq 'Capture') { Start-Sleep -Milliseconds 650; Save-Capture 'current.png' }
