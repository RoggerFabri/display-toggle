# Windows Makefile for display-toggle — mirrors mac/Makefile.
#
#   make            build (Release)
#   make clean      remove build output
#   make install    publish to %LOCALAPPDATA%\display-toggle + register login autostart
#   make uninstall  remove the install and autostart entry
#   make run        stop any running instance, build, and launch
#   make test       run the unit tests (DisplayToggle.Tests)
#
# Requires the .NET SDK (dotnet) and make. OS-specific steps run via PowerShell,
# so the recipes work whether make uses sh (Git Bash) or cmd as its shell.

CONFIG ?= Release
PS      = powershell -NoProfile -ExecutionPolicy Bypass

.PHONY: all build clean install uninstall run test

all: build

build:
	dotnet build -c $(CONFIG)

test:
	dotnet test DisplayToggle.Tests -c $(CONFIG)

clean:
	$(PS) -Command "Remove-Item -Recurse -Force bin,obj,DisplayToggle.Tests/bin,DisplayToggle.Tests/obj -ErrorAction SilentlyContinue"

# No build prerequisite: install.ps1 stops the running app first (it locks bin), then publishes.
install:
	$(PS) -File scripts/install.ps1 -Config $(CONFIG)

uninstall:
	$(PS) -File scripts/uninstall.ps1

# run.ps1 stops any running instance first (releasing the exe lock) before building.
run:
	$(PS) -File scripts/run.ps1 -Config $(CONFIG)
