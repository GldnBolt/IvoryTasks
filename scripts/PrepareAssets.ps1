$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'packaging/Assets'
New-Item -ItemType Directory -Force $assetDir | Out-Null
foreach ($spec in @(@('StoreLogo.png',50),@('Square44.png',44),@('Square150.png',150))) {
    $size = [int]$spec[1]
    $bitmap = [System.Drawing.Bitmap]::new($size,$size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.ColorTranslator]::FromHtml('#F0ECE3'))
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $font = [System.Drawing.Font]::new('Georgia',[single]($size * 0.53),[System.Drawing.FontStyle]::Regular,[System.Drawing.GraphicsUnit]::Pixel)
    $brush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#292825'))
    $graphics.DrawString('i.',$font,$brush,[single]($size * 0.22),[single]($size * 0.12))
    $bitmap.Save((Join-Path $assetDir $spec[0]),[System.Drawing.Imaging.ImageFormat]::Png)
    $font.Dispose(); $brush.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$bitmap = [System.Drawing.Bitmap]::new(640,640)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.Clear([System.Drawing.ColorTranslator]::FromHtml('#FAF8F3'))
$graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$font = [System.Drawing.Font]::new('Segoe UI',22)
$display = [System.Drawing.Font]::new('Georgia',36)
$brush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#292825'))
$graphics.DrawString('ivory.',$display,$brush,40,32)
$graphics.DrawString("My tasks`n`nToday - 3 tasks`n`nStudy SQL`nGo to the gym`nReview work tasks`n`nOpen Ivory     Refresh",$font,$brush,40,130)
$bitmap.Save((Join-Path $assetDir 'WidgetPreview.png'),[System.Drawing.Imaging.ImageFormat]::Png)
$font.Dispose(); $display.Dispose(); $brush.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
