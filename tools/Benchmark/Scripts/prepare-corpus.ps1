# Usage: pwsh prepare-corpus.ps1 [work directory]
# Writes the benchmark corpus into the work directory (default: $env:TARJAMOT_BENCH or
# tarjamot-native-benchmark in the temp directory) and checks it against the hashes in
# X86-PERFORMANCE.md:
#   article-cs.txt  first 3,147 words of the Czech Wikipedia article "Karel IV." (revision 26291369)
#   article-zh.txt  the Chinese Wikipedia article on the Great Wall (U+957F U+57CE) in zh-Hans (revision 94640681)
#   email-cs.txt    the first 108 words of article-cs.txt
#   email-cs.html   a hand-written Czech email as innerHTML writes it (copied from this directory)
# The Wikipedia API serves only the current revision of a plain-text extract; when an article has
# changed, the hashes differ and the measurements are not comparable with the recorded ones. Keep
# the work directory, or the corpus files, to compare later runs.
param([string]$Directory = ($env:TARJAMOT_BENCH ?? (Join-Path ([IO.Path]::GetTempPath()) 'tarjamot-native-benchmark')))

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Directory | Out-Null
$encoding = New-Object System.Text.UTF8Encoding $false
$userAgent = 'Tarjamot benchmark'

function Get-Extract([string]$language, [string]$title, [string]$variant)
{
	$uri = "https://$language.wikipedia.org/w/api.php?action=query&prop=extracts&explaintext=1&format=json&formatversion=2&titles=$([Uri]::EscapeDataString($title))"
	if ($variant) { $uri += "&variant=$variant" }
	$response = Invoke-RestMethod -Uri $uri -UserAgent $userAgent
	# Without section headings (== ... ==) and empty lines.
	$response.query.pages[0].extract -split "`n" | Where-Object { $_.Trim() -and $_ -notmatch '^\s*=+.*=+\s*$' }
}

$czech = Get-Extract 'cs' 'Karel IV.' $null
$words = 0
$lines = @()
foreach ($line in $czech)
{
	$lineWords = @($line -split '\s+' | Where-Object { $_ })
	if ($words + $lineWords.Count -gt 3147)
	{
		# 0..-1 would select the first and the last word, not none.
		if ($words -lt 3147)
		{
			$lines += ($lineWords[0..(3147 - $words - 1)] -join ' ')
		}
		break
	}
	$lines += $line
	$words += $lineWords.Count
}
[IO.File]::WriteAllText((Join-Path $Directory 'article-cs.txt'), ($lines -join "`n") + "`n", $encoding)
$emailWords = ($lines -join "`n") -split '\s+' | Where-Object { $_ }
[IO.File]::WriteAllText((Join-Path $Directory 'email-cs.txt'), ($emailWords[0..107] -join ' ') + "`n", $encoding)
$chinese = Get-Extract 'zh' "$([char]0x957F)$([char]0x57CE)" 'zh-hans'
[IO.File]::WriteAllText((Join-Path $Directory 'article-zh.txt'), ($chinese -join "`n") + "`n", $encoding)
Copy-Item (Join-Path $PSScriptRoot 'email-cs.html') $Directory -Force

$expected = @{
	'article-cs.txt' = 'E22E3DB95A66B8FB8BFFE517D7E8DC553E529F04EC6BBCFF87DD43970CD5B130'
	'article-zh.txt' = 'DADFB0DAB49905EA8E22FA2109520FAD5C22BC34D6E7B75D194B7DEACAFD3324'
	'email-cs.txt' = '944F0545E9459B1457EB0D9C06FA9A73B2024752DFA26B509E387F6714181E79'
	'email-cs.html' = '1F5DCDB0DC31D2C20E1CE8E6C7E016D61E5F6BD1BE41248F9CB3AA64081F6441'
}
foreach ($file in $expected.Keys)
{
	$hash = (Get-FileHash (Join-Path $Directory $file) -Algorithm SHA256).Hash
	$state = if ($hash.StartsWith($expected[$file])) { 'matches' } else { 'DIFFERS from the recorded corpus' }
	Write-Host "$file $hash $state"
}
