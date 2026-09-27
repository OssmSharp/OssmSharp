.DEFAULT_GOAL := publish

CONFIGURATION ?= Release
RUNTIME ?= osx-arm64
MACOS_API_VERSION ?= 27.0
DEVELOPER_DIR ?= /Applications/Xcode.app/Contents/Developer

CLI := ./src/OssmSharp.Cli/bin/$(CONFIGURATION)/net10.0-macos$(MACOS_API_VERSION)/$(RUNTIME)/OssmSharp.Cli.app/Contents/MacOS/OssmSharp.Cli

export DEVELOPER_DIR CLI DEVICE_ID

.PHONY: help publish list identify prompt require-publish require-device-id

help:
	@printf '%s\n' \
		'make publish                    Publish the self-contained macOS BLE CLI (default).' \
		'make list                       Discover advertising Stock devices.' \
		'make identify DEVICE_ID=...      Read the selected device identity, then disconnect.' \
		'make prompt DEVICE_ID=...        Connect and open the foreground prompt.' \
		'' \
		'Run publish again after source changes. Keep the entire .app bundle together.' \
		'Defaults: CONFIGURATION=Release RUNTIME=osx-arm64 MACOS_API_VERSION=27.0.' \
		'Override DEVELOPER_DIR to select another compatible Xcode installation.' \
		'Xcode 27 bindings are preview; the minimum runtime OS remains macOS 26.'

publish:
	@test "$$(uname -s)" = Darwin || { printf '%s\n' 'This Makefile publishes the macOS BLE CLI; run it on macOS.' >&2; exit 1; }
	dotnet publish src/OssmSharp.Cli/OssmSharp.Cli.csproj \
		-c "$(CONFIGURATION)" \
		-r "$(RUNTIME)" \
		--self-contained true \
		-p:EnableStockBle=true \
		-p:StockMacOSApiVersion="$(MACOS_API_VERSION)" \
		-p:TrimMode=copy \
		-bl:.artifacts/cli-publish-{}.binlog
	@test -x "$$CLI" || { printf '%s\n' 'Publish did not produce the expected executable in its .app bundle.' >&2; exit 1; }
	@printf 'Published CLI: %s\n' "$$CLI"

require-publish:
	@test -x "$$CLI" || { printf '%s\n' 'Run make publish first for the selected configuration.' >&2; exit 1; }

require-device-id:
	@test -n "$$DEVICE_ID" || { printf '%s\n' 'Set DEVICE_ID to the platform ID printed by make list.' >&2; exit 2; }

list: require-publish
	@exec "$$CLI" devices list

identify: require-device-id require-publish
	@exec "$$CLI" devices get "$$DEVICE_ID"

prompt: require-device-id require-publish
	@exec "$$CLI" prompt --bluetooth "$$DEVICE_ID"
