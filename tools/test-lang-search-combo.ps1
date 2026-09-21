# Headless regression check for the language-dropdown filter design (production GlossaryManagerWindow):
# a persistent plain TextBox OUTSIDE the dropdowns takes all keyboard input (window surface —
# no Popup-activation dependency that Studio host breaks), each ComboBox has its own
# ListCollectionView; typing only changes Filter, and the selected language is PINNED into the
# narrowed result because ComboBox silently rejects SelectedItem pointing at a filtered-out row.
# Run with powershell (STA). Mirrors .xaml/.cs without Studio/SQLite/HandyControl deps.
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml, System.Windows.Forms

$itemTemplate = [System.Windows.Markup.XamlReader]::Parse(@'
<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <TextBlock Text="{Binding Label}" TextTrimming="CharacterEllipsis"/>
</DataTemplate>
'@)

if (-not ('LangItemX' -as [type])) {
Add-Type -TypeDefinition @'
public class LangItemX {
    public string Code { get; set; }
    public string Label { get; set; }
    public override string ToString() { return Label; }
}
'@
}
$src = New-Object System.Collections.Generic.List[object]
foreach ($p in @(@('en-US','English (United States)  ·  en-US'),
                 @('de-DE','German (Germany)  ·  de-DE'),
                 @('zh-CN','Chinese (Simplified, PRC)  ·  zh-CN'),
                 @('zh-HK','Chinese (Traditional, Hong Kong S.A.R.)  ·  zh-HK'),
                 @('fr-FR','French (France)  ·  fr-FR'))) {
    $o = [LangItemX]::new(); $o.Code = $p[0]; $o.Label = $p[1]
    $src.Add($o) | Out-Null
}

$tb = New-Object System.Windows.Controls.TextBox   # = LangFilterBox (plain surface TextBox)
$tb.Width = 132; $tb.Height = 30

$comboA = New-Object System.Windows.Controls.ComboBox   # = SrcCombo
$comboB = New-Object System.Windows.Controls.ComboBox   # = TgtCombo
foreach ($c in @($comboA, $comboB)) {
    $c.Width = 188; $c.Height = 30
    $c.ItemTemplate = $itemTemplate
    $c.SelectedValuePath = 'Code'
    $c.IsTextSearchEnabled = $false
    $c.IsSynchronizedWithCurrentItem = $false
}
$viewA = [System.Windows.Data.ListCollectionView]::new($src)
$viewB = [System.Windows.Data.ListCollectionView]::new($src)
$comboA.ItemsSource = $viewA
$comboB.ItemsSource = $viewB

# production ReapplyLangFilter/ApplyViewFilter/RestoreSelection mirror
$script:q = ''
$script:pinA = ''   # _lastSrc
$script:pinB = ''   # _lastTgt
$predA = [System.Predicate[object]]{
    param($item)
    if ([string]::IsNullOrEmpty($script:q)) { return $true }
    if ($null -eq $item) { return $false }
    if ($script:pinA -and ($item.Code -ieq $script:pinA)) { return $true }
    return ($item.Label.ToLowerInvariant().Contains($script:q)) -or ($item.Code.ToLowerInvariant().Contains($script:q))
}
$predB = [System.Predicate[object]]{
    param($item)
    if ([string]::IsNullOrEmpty($script:q)) { return $true }
    if ($null -eq $item) { return $false }
    if ($script:pinB -and ($item.Code -ieq $script:pinB)) { return $true }
    return ($item.Label.ToLowerInvariant().Contains($script:q)) -or ($item.Code.ToLowerInvariant().Contains($script:q))
}
function Restore($combo, $pred) {
    if ($null -ne $combo.SelectedItem) { return }
    $pin = if ($pred -eq $predA) { $script:pinA } else { $script:pinB }
    if ([string]::IsNullOrEmpty($pin)) { return }
    foreach ($i in $src) { if ($i.Code -ieq $pin) { $combo.SelectedItem = $i; return } }
}
function Reapply {
    $script:q = "$($tb.Text)".Trim().ToLowerInvariant()
    $empty = [string]::IsNullOrEmpty($script:q)
    $viewA.Filter = $(if ($empty) { $null } else { $predA })
    $viewB.Filter = $(if ($empty) { $null } else { $predB })
    $viewA.Refresh(); $viewB.Refresh()
    Restore $comboA $predA; Restore $comboB $predB
}
$tb.Add_TextChanged({ Reapply })
# production LangChanged keeps the authoritative code + repins
$comboA.Add_SelectionChanged({ param($s,$e) if ($s.SelectedValue) { $script:pinA = $s.SelectedValue; Reapply } })

$panel = New-Object System.Windows.Controls.StackPanel
$panel.Children.Add($tb) | Out-Null
$panel.Children.Add($comboA) | Out-Null
$panel.Children.Add($comboB) | Out-Null
$w = New-Object System.Windows.Window
$w.Content = $panel
$w.Width = 500; $w.Height = 300
$w.Show()
$w.Activate() | Out-Null

$fail = 0
function Assert([bool]$cond, [string]$msg) {
    if ($cond) { Write-Host "PASS $msg" } else { Write-Host "FAIL $msg"; $script:fail = 1 }
}
function Pump([int]$ms = 200) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $ms) {
        [System.Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([System.Action]{
        }, [System.Windows.Threading.DispatcherPriority]::Background) | Out-Null
        Start-Sleep -Milliseconds 15
    }
}

# 1) keyboard lands directly in the plain filter box (no popup, no combobox focus games)
$null = $tb.Focus()
$null = [System.Windows.Input.Keyboard]::Focus($tb)
Pump 100
Assert ([System.Windows.Input.Keyboard]::FocusedElement -eq $tb) 'keyboard focus on LangFilterBox'

# 2) REAL English keystrokes land fully (the thing that never worked inside the dropdown popup)
[System.Windows.Forms.SendKeys]::SendWait('ger')
Pump 300
Write-Host ("filter box text after SendKeys 'ger': '" + $tb.Text + "'")
$isAscii = $tb.Text -cmatch '^[ -~]*$'
if ($isAscii) { Assert ($tb.Text -eq 'ger') "english keystrokes all land in plain TextBox (got '$($tb.Text)')" }
else { Assert (-not [string]::IsNullOrEmpty($tb.Text)) "IME keystrokes land in plain TextBox (got '$($tb.Text)')" }

# 3) both dropdown views narrow live from the SAME keystrokes
Assert ($viewA.Count -eq 1 -and $viewB.Count -eq 1) "filter 'ger' -> both dropdowns 1 item (got A=$($viewA.Count) B=$($viewB.Count))"

# 4) typed text survives view Refresh (old bug: reassigning ItemsSource wiped it)
Assert ($tb.Text.Length -ge 2) 'typed text survives view Refresh'

# 5) picking the narrowed row yields the right code
$comboA.SelectedIndex = 0
Assert ($comboA.SelectedValue -eq 'de-DE') "select narrowed row -> SelectedValue de-DE (got $($comboA.SelectedValue))"

# 6) backspace edits live
[System.Windows.Forms.SendKeys]::SendWait('{BACKSPACE}')
Pump 200
Write-Host ("filter box text after backspace: '" + $tb.Text + "'")
Assert ($tb.Text -eq 'ge') "backspace edits the box (got '$($tb.Text)')"

# 7) pin regression (probe-confirmed landmine): an unrelated filter must NOT blank the selection —
#    de-DE stays visible in the narrowed list and gets written back by RestoreSelection.
$tb.Text = 'fr'
Pump 200
Assert ($viewA.Count -eq 2) "'fr' shows French + pinned German (got A=$($viewA.Count))"
Assert ($comboA.SelectedValue -eq 'de-DE') "selection survives unrelated filter (got $($comboA.SelectedValue))"

# 8) clearing restores the full list, selection intact
$tb.Text = ''
Pump 200
Assert ($viewA.Count -eq 5 -and $viewB.Count -eq 5) "clear -> 5 items both dropdowns (got A=$($viewA.Count) B=$($viewB.Count))"
Assert ($comboA.SelectedValue -eq 'de-DE') "still de-DE after clear (got $($comboA.SelectedValue))"

# 9) case-insensitivity through the same predicate
$tb.Text = 'FR'
Pump 200
Assert ($viewA.Count -eq 2) "filter 'FR' case-insensitive -> French + pinned de (got $($viewA.Count))"
$tb.Text = 'chinese'
Pump 200
Assert ($viewA.Count -eq 3) "filter 'chinese' -> zh-CN + zh-HK + pinned de-DE (got $($viewA.Count))"

$w.Close()
if ($fail -eq 0) { Write-Host 'ALL PASS' } else { Write-Host 'FAILURES PRESENT'; exit 1 }
