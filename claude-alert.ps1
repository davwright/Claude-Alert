# Claude Alert - Subtle tone + edge flash across all monitors
# Triggered by Claude Code hooks when Claude needs user input

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName System.Windows.Forms

# --- Play a quiet notification tone via WAV (respects system volume) ---
$beepJob = Start-Job -ScriptBlock {
    $sampleRate = 22050
    $duration = 0.08          # 80ms total
    $freq = 880               # A5
    $amplitude = 800          # very quiet (max ~32767)
    $samples = [int]($sampleRate * $duration)
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    # WAV header
    $dataSize = $samples * 2
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes('RIFF'))
    $bw.Write([int](36 + $dataSize))
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
    $bw.Write([int]16); $bw.Write([int16]1); $bw.Write([int16]1)
    $bw.Write([int]$sampleRate); $bw.Write([int]($sampleRate*2))
    $bw.Write([int16]2); $bw.Write([int16]16)
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes('data'))
    $bw.Write([int]$dataSize)
    for ($i = 0; $i -lt $samples; $i++) {
        $t = $i / $sampleRate
        $envelope = 1.0 - ($i / $samples)  # fade out
        $val = [int16]($amplitude * $envelope * [Math]::Sin(2 * [Math]::PI * $freq * $t))
        $bw.Write($val)
    }
    $ms.Position = 0
    $player = New-Object System.Media.SoundPlayer($ms)
    $player.PlaySync()
    $bw.Dispose(); $ms.Dispose()
}

# --- Flash edges on ALL monitors ---
foreach ($screen in [System.Windows.Forms.Screen]::AllScreens) {
    $bounds = $screen.Bounds
    $borderWidth = 6

    # Create 4 border windows (top, bottom, left, right) for this screen
    $sides = @(
        @{ Left = $bounds.X; Top = $bounds.Y; Width = $bounds.Width; Height = $borderWidth }                                    # top
        @{ Left = $bounds.X; Top = $bounds.Y + $bounds.Height - $borderWidth; Width = $bounds.Width; Height = $borderWidth }     # bottom
        @{ Left = $bounds.X; Top = $bounds.Y; Width = $borderWidth; Height = $bounds.Height }                                    # left
        @{ Left = $bounds.X + $bounds.Width - $borderWidth; Top = $bounds.Y; Width = $borderWidth; Height = $bounds.Height }     # right
    )

    foreach ($side in $sides) {
        $window = New-Object System.Windows.Window
        $window.WindowStyle = 'None'
        $window.AllowsTransparency = $true
        $window.Background = [System.Windows.Media.Brushes]::Transparent
        $window.Topmost = $true
        $window.ShowInTaskbar = $false
        $window.ResizeMode = 'NoResize'
        $window.Left = $side.Left
        $window.Top = $side.Top
        $window.Width = $side.Width
        $window.Height = $side.Height

        # Orange glow border
        $border = New-Object System.Windows.Controls.Border
        $border.Background = [System.Windows.Media.SolidColorBrush]::new(
            [System.Windows.Media.Color]::FromArgb(220, 255, 140, 0)
        )
        $window.Content = $border

        # Fade in/out animation
        $fadeIn = New-Object System.Windows.Media.Animation.DoubleAnimation
        $fadeIn.From = 0.0
        $fadeIn.To = 1.0
        $fadeIn.Duration = [System.Windows.Duration]::new([TimeSpan]::FromMilliseconds(200))

        $fadeOut = New-Object System.Windows.Media.Animation.DoubleAnimation
        $fadeOut.From = 1.0
        $fadeOut.To = 0.0
        $fadeOut.Duration = [System.Windows.Duration]::new([TimeSpan]::FromMilliseconds(600))
        $fadeOut.BeginTime = [TimeSpan]::FromMilliseconds(500)

        $storyboard = New-Object System.Windows.Media.Animation.Storyboard
        [System.Windows.Media.Animation.Storyboard]::SetTarget($fadeIn, $window)
        [System.Windows.Media.Animation.Storyboard]::SetTargetProperty($fadeIn,
            [System.Windows.PropertyPath]::new('Opacity'))
        [System.Windows.Media.Animation.Storyboard]::SetTarget($fadeOut, $window)
        [System.Windows.Media.Animation.Storyboard]::SetTargetProperty($fadeOut,
            [System.Windows.PropertyPath]::new('Opacity'))
        $storyboard.Children.Add($fadeIn)
        $storyboard.Children.Add($fadeOut)

        # Close window when animation completes
        $storyboard.Add_Completed({
            param($s, $e)
            $window.Close()
        }.GetNewClosure())

        $window.Add_Loaded({
            param($s, $e)
            $storyboard.Begin()
        }.GetNewClosure())

        $window.Show()
    }
}

# Run the WPF dispatcher to process animations
$dispatcher = [System.Windows.Threading.Dispatcher]::CurrentDispatcher
$timer = New-Object System.Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromMilliseconds(1800)
$timer.Add_Tick({
    [System.Windows.Threading.Dispatcher]::CurrentDispatcher.InvokeShutdown()
})
$timer.Start()
[System.Windows.Threading.Dispatcher]::Run()

# Clean up beep job
$beepJob | Wait-Job -Timeout 3 | Remove-Job -Force -ErrorAction SilentlyContinue
