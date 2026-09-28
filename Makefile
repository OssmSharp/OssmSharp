.DEFAULT_GOAL := publish

CONFIGURATION ?= Release
RUNTIME ?= osx-arm64
DEVELOPER_DIR ?= /Applications/Xcode.app/Contents/Developer

CLI := ./src/OssmSharp.Cli/bin/$(CONFIGURATION)/net10.0-macos26.0/$(RUNTIME)/OssmSharp.Cli.app/Contents/MacOS/OssmSharp.Cli

export DEVELOPER_DIR CLI DEVICE_ID

.PHONY: help publish list prompt require-publish

help:
	@printf '%s\n' \
		'make publish                    Publish the macOS BLE CLI (default).' \
		'make list                       Discover advertising OEM devices without connecting.' \
		'make prompt [DEVICE_ID=...]      Connect to the selected or sole discovered device.' \
		'' \
		'Run publish again after source changes. Keep the entire .app bundle together.' \
		'Defaults: CONFIGURATION=Release RUNTIME=osx-arm64; macOS 26 or newer.' \
		'Override DEVELOPER_DIR to select another compatible Xcode installation.'

publish:
	@test "$$(uname -s)" = Darwin || { printf '%s\n' 'This Makefile publishes the macOS BLE CLI; run it on macOS.' >&2; exit 1; }
	dotnet publish src/OssmSharp.Cli/OssmSharp.Cli.csproj \
		-c "$(CONFIGURATION)" \
		-r "$(RUNTIME)" \
		-p:EnableStockBle=true \
		-bl:.artifacts/cli-publish-{}.binlog
	@test -x "$$CLI" || { printf '%s\n' 'Publish did not produce the expected executable in its .app bundle.' >&2; exit 1; }
	@printf 'Published CLI: %s\n' "$$CLI"

require-publish:
	@test -x "$$CLI" || { printf '%s\n' 'Run make publish first for the selected configuration.' >&2; exit 1; }

list: require-publish
	@exec "$$CLI" list

prompt: require-publish
	@if [ -n "$$DEVICE_ID" ]; then exec "$$CLI" prompt --device "$$DEVICE_ID"; else exec "$$CLI" prompt; fi
