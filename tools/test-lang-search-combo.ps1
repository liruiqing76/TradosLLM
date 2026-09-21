# Headless A/B check of the LangSearchCombo template + filter logic
# (mirrors GlossaryManagerWindow.xaml / .cs without Studio/SQLite deps).
# Run with STA powershell (default for powershell.exe).
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml

$styleXaml = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'lang-search-combo-style.txt')
$style = [System.Windows.Markup.XamlReader]::Parse($styleXaml)

$src = New-Object System.Collections.ArrayList
foreach ($p in @(@('en-US','English (United States)  ·  en-US'),
                 @('de-DE','German (Germany)  ·  de-DE'),
                 @('zh-CN','Chinese (Simplified, PRC)  ·  zh-CN'),
                 @('zh-HK','Chinese (Traditional, Hong Kong S.A.R.)  ·  zh-HK'),
                 @('fr-FR','French (France)  ·  fr-FR'))) {
    [void]$src.Add([pscustomobject]@{ Code = $p[0]; Label = $p[1] })
}

$combo = New-Object System.Windows.Controls.ComboBox
$combo.Style = $style
$combo.DisplayMemberPath = 'Label'
$combo.SelectedValuePath = 'Code'

$view = [System.Windows.Data.ListCollectionView]::new($src)
$combo.IsSynchronizedWithCurrentItem = $false
$combo.ItemsSource = $view

# production-equivalent filter wiring
$script:q = ''
$view.Filter = [System.Predicate[object]]{
    param($item)
    $qq = $script:q
    if ([string]::IsNullOrEmpty($qq)) { return $true }
    return ($item.Label.ToLowerInvariant().Contains($qq)) -or ($item.Code.ToLowerInvariant().Contains($qq))
}

function Set-Query([string]$text) {
    $script:q = $text.Trim().ToLowerInvariant()
    $view.Refresh()
}

$w = New-Object System.Windows.Window
$w.Content = $combo
$w.Width = 400; $w.Height = 100
$w.Show()
$combo.ApplyTemplate() | Out-Null

$fail = 0
function Assert([bool]$cond, [string]$msg) {
    if ($cond) { Write-Host "PASS $msg" } else { Write-Host "FAIL $msg"; $script:fail = 1 }
}

# 1) dropdown opens and the visible search box exists in the template
$combo.IsDropDownOpen = $true
[System.Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke(
    [System.Action]{ $w.UpdateLayout() },
    [System.Windows.Threading.DispatcherPriority]::Loaded) | Out-Null
$search = $combo.Template.FindName('LangSearchBox', $combo)
Assert ($null -ne $search) 'LangSearchBox found inside dropdown template'
Assert ($combo.IsDropDownOpen) 'dropdown openable'

# 2) typing filters the view (same code path as TextChanged handler)
if ($null -ne $search) {
    $search.Text = 'zh';     Set-Query $search.Text
    Assert ($view.Count -eq 2) "filter 'zh' -> 2 (got $($view.Count))"
    $search.Text = 'german'; Set-Query $search.Text
    Assert ($view.Count -eq 1) "filter 'german' -> 1 (got $($view.Count))"
    $search.Text = 'FR';     Set-Query $search.Text
    Assert ($view.Count -eq 1) "filter 'FR' case-insensitive -> 1 (got $($view.Count))"
    $search.Text = '';       Set-Query $search.Text
    Assert ($view.Count -eq 5) "clear -> 5 (got $($view.Count))"
}

# 3) selection + SelectedValuePath still work
$combo.SelectedIndex = 1
Assert ($combo.SelectedValue -eq 'de-DE') "select row -> SelectedValue de-DE (got $($combo.SelectedValue))"

# 4) hint placeholder visible when query empty (trigger wired)
$hint = $combo.Template.FindName('SearchHint', $combo)
Assert ($null -ne $hint) 'SearchHint element present'

$w.Close()
if ($fail -eq 0) { Write-Host 'ALL PASS' } else { Write-Host 'FAILURES PRESENT'; exit 1 }
