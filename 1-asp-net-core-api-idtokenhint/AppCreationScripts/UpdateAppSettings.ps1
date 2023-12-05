[CmdletBinding()]
param(
    [Parameter(Mandatory=$True)] [string] $ConfigFile,
    [Parameter(Mandatory=$True)] [System.Collections.HashTable] $Settings
)

Write-Host "Updating the sample code ($configFile)"

Function UpdateLine([string] $line, [string] $value) {
    $index = $line.IndexOf('=')
    $delimiter = ';'
    if ($index -eq -1) {
        $index = $line.IndexOf(':')
        $delimiter = ','
    }
    if ($index -ige 0) {
        $line = $line.Substring(0, $index+1) + " "+'"'+$value+'"'+$delimiter
    }
    return $line
}

Function UpdateTextFile([string] $configFilePath, [System.Collections.HashTable] $dictionary) {
    $lines = Get-Content $configFilePath
    $index = 0
    while($index -lt $lines.Length) {
        $line = $lines[$index]
        foreach($key in $dictionary.Keys) {
            if ($line.Contains($key)) {
                $lines[$index] = UpdateLine $line $dictionary[$key]
            }
        }
        $index++
    }
    Set-Content -Path $configFilePath -Value $lines -Force
}

UpdateTextFile $ConfigFile $Settings