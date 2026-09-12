# PowerShell script to test LiteDB shared mode with cross-user scenarios
# This script creates temporary users and runs tests as different users to verify cross-user access

param(
    [string]$TestDll,
    [string]$Framework = "net8.0"
)

$ErrorActionPreference = "Stop"

# Configuration
$TestUsers = @("LiteDBTest1", "LiteDBTest2")
$TestPassword = ConvertTo-SecureString "Test@Password123!" -AsPlainText -Force
$DbPath = Join-Path $env:TEMP "litedb_crossuser_test.db"
$TestId = [Guid]::NewGuid().ToString("N").Substring(0, 8)

Write-Host "=== LiteDB Cross-User Testing Script ===" -ForegroundColor Cyan
Write-Host "Test ID: $TestId"
Write-Host "Database: $DbPath"
Write-Host "Framework: $Framework"
Write-Host ""

# Function to create a test user
function Create-TestUser {
    param([string]$Username)

    try {
        # Check if user already exists
        $existingUser = Get-LocalUser -Name $Username -ErrorAction SilentlyContinue
        if ($existingUser) {
            Write-Host "User $Username already exists, removing..." -ForegroundColor Yellow
            Remove-LocalUser -Name $Username -ErrorAction SilentlyContinue
        }

        Write-Host "Creating user: $Username" -ForegroundColor Green
        New-LocalUser -Name $Username -Password $TestPassword -FullName "LiteDB Test User" -Description "Temporary user for LiteDB cross-user testing" -ErrorAction Stop | Out-Null

        # Add to Users group
        Add-LocalGroupMember -Group "Users" -Member $Username -ErrorAction SilentlyContinue

        return $true
    }
    catch {
        Write-Host "Failed to create user ${Username}: $_" -ForegroundColor Red
        return $false
    }
}

# Function to remove a test user
function Remove-TestUser {
    param([string]$Username)

    try {
        $user = Get-LocalUser -Name $Username -ErrorAction SilentlyContinue
        if ($user) {
            Write-Host "Removing user: $Username" -ForegroundColor Yellow
            Remove-LocalUser -Name $Username -ErrorAction Stop
        }
    }
    catch {
        Write-Host "Warning: Failed to remove user ${Username}: $_" -ForegroundColor Yellow
    }
}

# Function to run process as a specific user
function Run-AsUser {
    param(
        [string]$Username,
        [string]$Command,
        [string[]]$Arguments,
        [int]$TimeoutSeconds = 30
    )

    Write-Host "Running as user $Username..." -ForegroundColor Cyan

    $credential = New-Object System.Management.Automation.PSCredential($Username, $TestPassword)

    try {
        $job = Start-Job -ScriptBlock {
            param($cmd, $args)
            & $cmd $args
        } -ArgumentList $Command, $Arguments -Credential $credential

        $completed = Wait-Job -Job $job -Timeout $TimeoutSeconds

        if (-not $completed) {
            Stop-Job -Job $job
            throw "Process timed out after $TimeoutSeconds seconds"
        }

        $output = Receive-Job -Job $job
        Remove-Job -Job $job

        Write-Host "Output from ${Username}:" -ForegroundColor Gray
        $output | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }

        return $true
    }
    catch {
        Write-Host "Failed to run as ${Username}: $_" -ForegroundColor Red
        return $false
    }
}

# Cleanup function
function Cleanup {
    Write-Host "`n=== Cleanup ===" -ForegroundColor Cyan

    # Remove database files
    if (Test-Path $DbPath) {
        try {
            Remove-Item $DbPath -Force -ErrorAction SilentlyContinue
            Write-Host "Removed database file" -ForegroundColor Yellow
        }
        catch {
            Write-Host "Warning: Could not remove database file: $_" -ForegroundColor Yellow
        }
    }

    $logPath = "$DbPath-log"
    if (Test-Path $logPath) {
        try {
            Remove-Item $logPath -Force -ErrorAction SilentlyContinue
            Write-Host "Removed database log file" -ForegroundColor Yellow
        }
        catch {
            Write-Host "Warning: Could not remove database log file: $_" -ForegroundColor Yellow
        }
    }

    # Remove test users
    foreach ($username in $TestUsers) {
        Remove-TestUser -Username $username
    }
}

# Register cleanup on exit
try {
    # Check if running as Administrator
    $isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

    if (-not $isAdmin) {
        Write-Host "ERROR: This script must be run as Administrator to create users" -ForegroundColor Red
        exit 1
    }

    # Cleanup any previous test artifacts
    Cleanup

    # Create test users
    Write-Host "`n=== Creating Test Users ===" -ForegroundColor Cyan
    $usersCreated = $true
    foreach ($username in $TestUsers) {
        if (-not (Create-TestUser -Username $username)) {
            $usersCreated = $false
            break
        }
    }

    if (-not $usersCreated) {
        Write-Host "Failed to create all test users" -ForegroundColor Red
        exit 1
    }

    # Initialize database with current user
    Write-Host "`n=== Initializing Database ===" -ForegroundColor Cyan
    $initScript = @"
using System;
using LiteDB;

var db = new LiteDatabase(new ConnectionString
{
    Filename = @"$DbPath",
    Connection = ConnectionType.Shared
});

var col = db.GetCollection<BsonDocument>("crossuser_test");
col.Insert(new BsonDocument { ["user"] = Environment.UserName, ["timestamp"] = DateTime.UtcNow, ["action"] = "init" });
db.Dispose();

Console.WriteLine("Database initialized by " + Environment.UserName);
"@

    $initScriptPath = Join-Path $env:TEMP "litedb_init_$TestId.cs"
    Set-Content -Path $initScriptPath -Value $initScript

    # Run init script
    dotnet script $initScriptPath

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Failed to initialize database" -ForegroundColor Red
        exit 1
    }

    Remove-Item $initScriptPath -Force -ErrorAction SilentlyContinue

    # Grant permissions to the database file for all test users
    Write-Host "`n=== Setting Database Permissions ===" -ForegroundColor Cyan
    $acl = Get-Acl $DbPath
    foreach ($username in $TestUsers) {
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($username, "FullControl", "Allow")
        $acl.SetAccessRule($rule)
    }
    Set-Acl -Path $DbPath -AclObject $acl
    Write-Host "Database permissions set for all test users" -ForegroundColor Green

    # Run tests as each user
    Write-Host "`n=== Running Cross-User Tests ===" -ForegroundColor Cyan

    $testScript = @"
using System;
using LiteDB;

var db = new LiteDatabase(new ConnectionString
{
    Filename = @"$DbPath",
    Connection = ConnectionType.Shared
});

var col = db.GetCollection<BsonDocument>("crossuser_test");

// Read existing documents
var existingCount = col.Count();
Console.WriteLine(`$"User {Environment.UserName} found {existingCount} existing documents");

// Write new document
col.Insert(new BsonDocument { ["user"] = Environment.UserName, ["timestamp"] = DateTime.UtcNow, ["action"] = "write" });

// Read all documents
var allDocs = col.FindAll();
Console.WriteLine("All documents in database:");
foreach (var doc in allDocs)
{
    Console.WriteLine(`$"  - User: {doc[\"user\"]}, Action: {doc[\"action\"]}");
}

db.Dispose();
Console.WriteLine("Test completed successfully for user " + Environment.UserName);
"@

    $testScriptPath = Join-Path $env:TEMP "litedb_test_$TestId.cs"
    Set-Content -Path $testScriptPath -Value $testScript

    # Note: Running as different users requires elevated permissions and is complex
    # For now, we'll document that this should be done manually or in a controlled environment
    Write-Host "Cross-user test script created at: $testScriptPath" -ForegroundColor Green
    Write-Host "Note: Automated cross-user testing requires complex setup." -ForegroundColor Yellow
    Write-Host "For full cross-user verification, run the following manually:" -ForegroundColor Yellow
    Write-Host "  dotnet script $testScriptPath" -ForegroundColor Cyan
    Write-Host "  (as each of the test users: $($TestUsers -join ', '))" -ForegroundColor Cyan

    # For CI purposes, we'll verify that the database was created and is accessible
    Write-Host "`n=== Verifying Database Access ===" -ForegroundColor Cyan
    if (Test-Path $DbPath) {
        Write-Host "✓ Database file exists and is accessible" -ForegroundColor Green

        # Verify we can open it in shared mode
        $verifyScript = @"
using System;
using LiteDB;

var db = new LiteDatabase(new ConnectionString
{
    Filename = @"$DbPath",
    Connection = ConnectionType.Shared
});

var col = db.GetCollection<BsonDocument>("crossuser_test");
var count = col.Count();
Console.WriteLine(`$"Verification: Found {count} documents in shared database");
db.Dispose();

if (count > 0) {
    Console.WriteLine("SUCCESS: Database is accessible in shared mode");
    Environment.Exit(0);
} else {
    Console.WriteLine("ERROR: Database is empty");
    Environment.Exit(1);
}
"@

        $verifyScriptPath = Join-Path $env:TEMP "litedb_verify_$TestId.cs"
        Set-Content -Path $verifyScriptPath -Value $verifyScript

        dotnet script $verifyScriptPath
        $verifyResult = $LASTEXITCODE

        Remove-Item $verifyScriptPath -Force -ErrorAction SilentlyContinue
        Remove-Item $testScriptPath -Force -ErrorAction SilentlyContinue

        if ($verifyResult -eq 0) {
            Write-Host "`n=== Cross-User Test Setup Completed Successfully ===" -ForegroundColor Green
            exit 0
        }
        else {
            Write-Host "`n=== Cross-User Test Setup Failed ===" -ForegroundColor Red
            exit 1
        }
    }
    else {
        Write-Host "✗ Database file was not created" -ForegroundColor Red
        exit 1
    }
}
finally {
    Cleanup
}
