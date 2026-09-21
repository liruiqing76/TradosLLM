# 无头计时：复现"点开语言下拉慢"——与 GlossaryManagerWindow 相同的 LangSearchCombo 模板
# + 460 条语言数据，测量打开/关闭/再打开耗时与实际生成的容器数（判断虚拟化是否生效）。
# 结果边跑边写 tools/test-lang-combo-open-speed.out，防卡死丢输出。STA PowerShell 运行。
param([string]$OutFile = (Join-Path $PSScriptRoot 'test-lang-combo-open-speed.out'))
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml

function Log([string]$m) {
    $m | Out-File -FilePath $OutFile -Append -Encoding UTF8
    [Console]::WriteLine($m)
}
"== test start $(Get-Date -Format 'HH:mm:ss.fff') ==" | Out-File -FilePath $OutFile -Encoding UTF8

try {
$styleXaml = @'
<ResourceDictionary
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <DataTemplate x:Key="LangItemTemplate">
        <TextBlock Text="{Binding Label}" TextTrimming="CharacterEllipsis"/>
    </DataTemplate>

    <!-- 与 GlossaryManagerWindow.xaml 的 LangSearchCombo 一致（画刷换字面量） -->
    <Style x:Key="LangSearchCombo" TargetType="ComboBox">
        <Setter Property="Height" Value="30"/>
        <Setter Property="VerticalAlignment" Value="Center"/>
        <Setter Property="ItemTemplate" Value="{StaticResource LangItemTemplate}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ComboBox">
                    <Grid>
                        <ToggleButton Focusable="False" ClickMode="Press"
                                      IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
                            <ToggleButton.Template>
                                <ControlTemplate TargetType="ToggleButton">
                                    <Border x:Name="ComboBorder" Background="#FFFAFBFD"
                                            BorderBrush="#FFCFD6DF" BorderThickness="1" CornerRadius="4">
                                        <Grid>
                                            <Grid.ColumnDefinitions>
                                                <ColumnDefinition Width="*"/>
                                                <ColumnDefinition Width="24"/>
                                            </Grid.ColumnDefinitions>
                                            <ContentPresenter Grid.Column="0" Margin="9,0,0,0"
                                                              VerticalAlignment="Center" HorizontalAlignment="Left"
                                                              Content="{Binding SelectedItem, RelativeSource={RelativeSource AncestorType=ComboBox}}"
                                                              ContentTemplate="{Binding ItemTemplate, RelativeSource={RelativeSource AncestorType=ComboBox}}"/>
                                            <Path Grid.Column="1" HorizontalAlignment="Center" VerticalAlignment="Center"
                                                  Data="M 0 0 L 4 4 L 8 0 Z" Fill="#FF7A869A"/>
                                        </Grid>
                                    </Border>
                                </ControlTemplate>
                            </ToggleButton.Template>
                        </ToggleButton>
                        <Popup x:Name="PART_Popup" AllowsTransparency="True" Placement="Bottom" Focusable="True"
                               IsOpen="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
                            <Border Background="#FFFAFBFD" BorderBrush="#FFDFE4EB"
                                    BorderThickness="1" CornerRadius="4" Margin="0,2,0,0"
                                    MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}">
                                <StackPanel>
                                    <Grid Margin="8,8,8,4">
                                        <TextBox x:Name="LangSearchBox" Height="26" FontSize="12" Padding="5,0" Focusable="True"
                                                 VerticalContentAlignment="Center"/>
                                        <TextBlock x:Name="SearchHint" Text="输入代码或名称过滤…" Foreground="#FF9AA5B1" FontSize="12"
                                                   Margin="8,0,0,0" VerticalAlignment="Center" IsHitTestVisible="False"
                                                   Visibility="Collapsed"/>
                                    </Grid>
                                    <Separator Margin="6,0"/>
                                    <ScrollViewer MaxHeight="260" CanContentScroll="True"
                                                  VirtualizingPanel.IsVirtualizing="True"
                                                  VirtualizingPanel.ScrollUnit="Item"
                                                  VerticalScrollBarVisibility="Auto">
                                        <ItemsPresenter/>
                                    </ScrollViewer>
                                </StackPanel>
                            </Border>
                        </Popup>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger SourceName="LangSearchBox" Property="Text" Value="">
                            <Setter TargetName="SearchHint" Property="Visibility" Value="Visible"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
</ResourceDictionary>
'@
$rd = [System.Windows.Markup.XamlReader]::Parse($styleXaml)

# A/B：A=现模板（默认 StackPanel 宿主，无虚拟化），B=加 ItemsPanel 虚拟化覆盖
$rdB = [System.Windows.Markup.XamlReader]::Parse($styleXaml)
$styleB = $rdB['LangSearchCombo']
$panelSetter = [System.Windows.Markup.XamlReader]::Parse(
    '<Setter xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Property="ItemsControl.ItemsPanel"><Setter.Value><ItemsPanelTemplate><VirtualizingStackPanel/></ItemsPanelTemplate></Setter.Value></Setter>')
$styleB.Setters.Add($panelSetter)

$items = New-Object System.Collections.ArrayList
$langs = @('English','German','French','Chinese','Japanese','Korean','Spanish','Italian','Russian','Portuguese','Arabic','Dutch','Swedish','Danish','Finnish','Norwegian','Polish','Czech','Hungarian','Turkish','Thai','Vietnamese','Indonesian','Malay','Hindi','Hebrew','Greek','Romanian','Bulgarian','Ukrainian')
$i = 0
while ($items.Count -lt 460) {
    $name = $langs[$i % $langs.Count] + " (variant " + $i + ")"
    $code = ("x" + $i + "-XX")
    [void]$items.Add([pscustomobject]@{ Code = $code; Label = "$name  ·  $code" })
    $i++
}

$combo = New-Object System.Windows.Controls.ComboBox
$combo.SelectedValuePath = 'Code'
$combo.Width = 238
$view = [System.Windows.Data.ListCollectionView]::new($items)
$combo.IsSynchronizedWithCurrentItem = $false
$combo.ItemsSource = $view
$combo.SelectedItem = $items[3]

$w = New-Object System.Windows.Window
$w.Content = $combo
$w.ShowActivated = $false
$w.WindowStartupLocation = 'Manual'
$w.Left = -32000; $w.Top = -32000
$w.Width = 500; $w.Height = 120
$w.Show()

# 测试环境无交互桌面：弹出层不抢焦点（不影响容器生成计时的结论）
$combo.Style = $rd['LangSearchCombo']
$combo.ApplyTemplate() | Out-Null
$popup = $combo.Template.FindName('PART_Popup', $combo)
if ($popup -ne $null) { $popup.Focusable = $false }
Log ("模板就绪, popup=" + ($popup -ne $null) + ", 条目=" + $items.Count)

function Pump([int]$maxIter = 200) {
    for ($k = 0; $k -lt $maxIter; $k++) {
        $frame = New-Object System.Windows.Threading.DispatcherFrame
        [System.Windows.Threading.Dispatcher]::CurrentDispatcher.BeginInvoke(
            [System.Windows.Threading.DispatcherPriority]::Background,
            [System.Action]{ $frame.Continue = $false }) | Out-Null
        [System.Windows.Threading.Dispatcher]::PushFrame($frame)
        if (-not $frame.Continue) { break }
    }
}

function Find-Visual($root, $type) {
    $count = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($root)
    for ($k = 0; $k -lt $count; $k++) {
        $c = [System.Windows.Media.VisualTreeHelper]::GetChild($root, $k)
        if ($c -is $type) { return $c }
        $r = Find-Visual -root $c -type $type
        if ($r -ne $null) { return $r }
    }
    return $null
}

function ReportPanel($tag) {
    # 从 ItemsPresenter 下找真正的条目宿主面板
    $presenter = $null
    if ($popup.Child -ne $null) {
        $presenter = Find-Visual -root $popup.Child -type ([System.Windows.Controls.ItemsPresenter])
    }
    $panel = $null
    if ($presenter -ne $null -and [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($presenter) -gt 0) {
        $panel = [System.Windows.Media.VisualTreeHelper]::GetChild($presenter, 0)
    }
    if ($panel -ne $null) {
        Log ("  {0} 条目宿主={1}, 实际生成容器={2}" -f $tag, $panel.GetType().Name, $panel.Children.Count)
    } else {
        Log ("  {0} 未找到条目宿主 Panel (presenter={1})" -f $tag, ($presenter -ne $null))
    }
}

$sw = New-Object System.Diagnostics.Stopwatch
foreach ($round in @(@('A-无ItemsPanel覆盖', $rd['LangSearchCombo']), @('B-ItemsPanel=VirtualizingStackPanel', $styleB))) {
    $tag = $round[0]
    $combo.Style = $round[1]
    $combo.ApplyTemplate() | Out-Null
    $popup = $combo.Template.FindName('PART_Popup', $combo)
    if ($popup -ne $null) { $popup.Focusable = $false }
    Log ("--- {0} ---" -f $tag)
    1..3 | ForEach-Object {
        $sw.Restart()
        $combo.IsDropDownOpen = $true
        Pump
        $openMs = $sw.ElapsedMilliseconds
        ReportPanel ("第" + $_ + "次")
        $sw.Restart()
        $combo.IsDropDownOpen = $false
        Pump
        $closeMs = $sw.ElapsedMilliseconds
        Log ("  第{0}次: 打开 {1} ms, 关闭 {2} ms" -f $_, $openMs, $closeMs)
    }
}
Log ("总条目: " + $items.Count)
$w.Close()
} catch {
    Log ("EXCEPTION: " + $_.Exception.ToString())
}
Log "== test end =="
