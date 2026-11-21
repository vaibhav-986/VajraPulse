$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing,System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PulseAdvancedNative {
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr FindWindow(string c,string t);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 public static IntPtr FindApp(){return FindWindow(null,"Vajra Pulse");}
 public static IntPtr FindDialog(string title){return FindWindow(null,title);}
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
'@
Add-Type -ReferencedAssemblies @([System.Windows.Automation.AutomationElement].Assembly.Location,[System.Windows.Automation.InvokePattern].Assembly.Location) -TypeDefinition @'
using System.Threading.Tasks;
using System.Windows.Automation;
public static class PulseAsyncInvoke {
 public static Task Start(InvokePattern pattern) { return Task.Run(() => pattern.Invoke()); }
}
'@
$handle = [PulseAdvancedNative]::FindApp()
if ($handle -eq [IntPtr]::Zero) { throw 'App not running.' }
[void][PulseAdvancedNative]::PostMessage($handle,0x312,[IntPtr]0x5650,[IntPtr]::Zero)
Start-Sleep -Milliseconds 650
$process=Get-Process VajraPulse | Select-Object -First 1
$condition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,[int]$process.Id)
$root=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,$condition)
if ($null -eq $root) { throw 'Visible app window not found.' }
function Read-Control($parent,[string]$name) {
 $c=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$name)
 return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c)
}
function Invoke-Pulse([string]$name,[switch]$Asynchronously) {
 $item=Read-Control $root $name
 if ($null -eq $item) { throw ('Missing control: '+$name) }
 $pattern=$item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
 if ($Asynchronously) { return [PulseAsyncInvoke]::Start($pattern) }
 $pattern.Invoke()
}
function Assert-Pulse([string]$name) {
 if ($null -eq (Read-Control $root $name)) { throw ('Expected control: '+$name) }
 'PASS: '+$name
}
function Capture-Pulse([string]$name) {
 Start-Sleep -Milliseconds 650
 $dpi=[PulseAdvancedNative]::SetThreadDpiAwarenessContext([IntPtr](-4))
 $r=$root.Current.BoundingRectangle
 $b=New-Object System.Drawing.Bitmap([int]$r.Width,[int]$r.Height)
 $g=[System.Drawing.Graphics]::FromImage($b)
 try { $g.CopyFromScreen([int]$r.Left,[int]$r.Top,0,0,$b.Size); $b.Save((Join-Path $PSScriptRoot $name),[System.Drawing.Imaging.ImageFormat]::Png) } finally { $g.Dispose();$b.Dispose(); [void][PulseAdvancedNative]::SetThreadDpiAwarenessContext($dpi) }
}
function Complete-Picker([string]$title,[string]$path) {
 $timer=[System.Diagnostics.Stopwatch]::StartNew()
 $native=[IntPtr]::Zero
 while ($native -eq [IntPtr]::Zero -and $timer.ElapsedMilliseconds -lt 5000) {
  $native=[PulseAdvancedNative]::FindDialog($title)
  if ($native -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 50 }
 }
 if ($native -eq [IntPtr]::Zero) { throw ('File picker not found: '+$title) }
 Start-Sleep -Milliseconds 300
 if (-not [PulseAdvancedNative]::SetForegroundWindow($native)) { throw 'Could not focus the test file picker.' }
 [System.Windows.Forms.SendKeys]::SendWait('%n')
 [System.Windows.Forms.SendKeys]::SendWait('^a')
 $escaped=''
 foreach($character in $path.ToCharArray()) { if ('+^%~(){}[]'.Contains([string]$character)) { $escaped+='{'+$character+'}' } else { $escaped+=$character } }
 [System.Windows.Forms.SendKeys]::SendWait($escaped)
 [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
 Start-Sleep -Milliseconds 800
}
$fixtureFolder=Join-Path ([System.IO.Path]::GetTempPath()) ('pulse-ui-fixtures-'+[Guid]::NewGuid().ToString('N'))
[void][System.IO.Directory]::CreateDirectory($fixtureFolder)
$file=Join-Path $fixtureFolder 'fixture-shelf.txt'
$calendar=Join-Path $fixtureFolder 'fixture-calendar.ics'
[System.IO.File]::WriteAllText($file,'DEMO: Safe local shelf test fixture. Removing the shortcut must leave this file intact.')
$start=[DateTime]::UtcNow.AddMinutes(30).ToString("yyyyMMdd'T'HHmmss'Z'")
$end=[DateTime]::UtcNow.AddMinutes(60).ToString("yyyyMMdd'T'HHmmss'Z'")
[System.IO.File]::WriteAllText($calendar,"BEGIN:VCALENDAR`r`nVERSION:2.0`r`nBEGIN:VEVENT`r`nUID:pulse-ui-demo`r`nSUMMARY:DEMO - Meeting preview test`r`nDTSTART:$start`r`nDTEND:$end`r`nURL:https://example.invalid/demo-meeting`r`nEND:VEVENT`r`nEND:VCALENDAR`r`n",(New-Object System.Text.UTF8Encoding($false)))
try {
 if ($null -ne (Read-Control $root 'Back to island')) { Invoke-Pulse 'Back to island'; Start-Sleep -Milliseconds 650 }
 if ($null -ne (Read-Control $root 'Expand Vajra Pulse')) { Invoke-Pulse 'Expand Vajra Pulse'; Start-Sleep -Milliseconds 650 }
 if ($null -eq (Read-Control $root 'Show file shelf')) { [void][PulseAdvancedNative]::PostMessage($handle,0x312,[IntPtr]0x5650,[IntPtr]::Zero); Start-Sleep -Milliseconds 650 }
 Invoke-Pulse 'Show file shelf'
 $pickerTask=Invoke-Pulse 'Add files' -Asynchronously
 Complete-Picker 'Add to Vajra file shelf' $file
 Assert-Pulse 'fixture-shelf.txt'
 Capture-Pulse 'advanced-shelf.png'
 Invoke-Pulse 'Remove shelf shortcut'
 if (-not [System.IO.File]::Exists($file)) { throw 'Shelf removal deleted the source file.' }
 'PASS: Removing the UI shortcut leaves its source file intact'
 Invoke-Pulse 'Show calendar'
 $pickerTask=Invoke-Pulse 'Import .ics' -Asynchronously
 Complete-Picker 'Import local calendar' $calendar
 Assert-Pulse 'DEMO - Meeting preview test'
 $join=Read-Control $root 'Join meeting'
 if (-not $join.Current.IsEnabled) { throw 'Safe meeting link did not enable Join.' }
 'PASS: Imported fixture exposes a join button without opening a website'
 Capture-Pulse 'advanced-calendar.png'
 Invoke-Pulse 'Clear'
 Assert-Pulse 'Import a calendar .ics file'
 Invoke-Pulse 'Show notification previews'
 Assert-Pulse 'Notification previews are off'
 Capture-Pulse 'advanced-alerts.png'
 Invoke-Pulse 'Open settings'
 Start-Sleep -Milliseconds 650
 $toggle=(Read-Control $root 'Hide message content').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
 if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { throw 'Content masking is not enabled by default.' }
 'PASS: Notification content is hidden by default'
 $toggle=(Read-Control $root 'Enable notification previews').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
 if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) { throw 'Notifications were unexpectedly enabled.' }
 'PASS: Windows notification permission has not been requested'
 Invoke-Pulse 'Test animation'
 Assert-Pulse 'Charging animation test'
 Capture-Pulse 'advanced-charging.png'
 'PASS: Charging animation preview is clearly labeled as a test'
 [void][PulseAdvancedNative]::PostMessage($handle,0x312,[IntPtr]0x5650,[IntPtr]::Zero)
 Start-Sleep -Milliseconds 650
 Invoke-Pulse 'Show music controls'
 Invoke-Pulse 'Collapse Vajra Pulse'
 'PASS: Advanced UI flows completed; no calendar account, browser, certificate, or system control changes were made'
} finally {
 [System.IO.Directory]::Delete($fixtureFolder,$true)
}
