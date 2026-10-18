BINARY     = display-toggle
SOURCES    = display-toggle.swift peer-logic.swift peer-link.swift update-logic.swift updater.swift
# Release builds pass VERSION=X.Y.Z; anything else is a dev build (no automatic update checks).
VERSION   ?= 0.0.0-dev
GEN        = gen
GEN_PLIST  = $(GEN)/Info.plist
EMBED      = -Xlinker -sectcreate -Xlinker __TEXT -Xlinker __info_plist -Xlinker $(GEN_PLIST)
DIST_ARCHIVE = dist/display-toggle-macos.tar.gz
PREFIX    ?= /usr/local
BINPATH    = $(PREFIX)/bin/$(BINARY)
LOCAL_USER := $(or $(SUDO_USER),$(shell whoami))
UID        := $(shell id -u $(LOCAL_USER))
USER_HOME  := $(shell eval echo ~$(LOCAL_USER))
LABEL      = com.$(LOCAL_USER).display-toggle
PLIST      = $(USER_HOME)/Library/LaunchAgents/$(LABEL).plist

.PHONY: all build clean install uninstall run test dist FORCE

all: build

build: $(BINARY)

# Info.plist is embedded (no .app bundle) so macOS sees the Bonjour / Local Network declarations.
# A generated copy carries CFBundleShortVersionString = $(VERSION); the tracked file stays as is.
$(BINARY): $(SOURCES) $(GEN_PLIST)
	swiftc $(SOURCES) -o $(BINARY) $(EMBED)

# Rewritten only when VERSION changes, so a different VERSION relinks and the same one doesn't.
$(GEN)/version: FORCE
	@mkdir -p $(GEN)
	@echo '$(VERSION)' | cmp -s - $@ || echo '$(VERSION)' > $@

$(GEN_PLIST): Info.plist $(GEN)/version
	cp Info.plist $@
	plutil -replace CFBundleShortVersionString -string '$(VERSION)' $@
	plutil -lint $@

# Release asset: a universal (arm64 + x86_64, macOS 13+) ad-hoc signed binary, alone in a tar.gz.
#   make dist VERSION=X.Y.Z  →  dist/display-toggle-macos.tar.gz
dist: $(SOURCES) $(GEN_PLIST)
	rm -rf $(GEN)/dist dist
	mkdir -p $(GEN)/dist dist
	swiftc $(SOURCES) -O -target arm64-apple-macos13.0 -o $(GEN)/dist/$(BINARY)-arm64 $(EMBED)
	swiftc $(SOURCES) -O -target x86_64-apple-macos13.0 -o $(GEN)/dist/$(BINARY)-x86_64 $(EMBED)
	lipo -create -output $(GEN)/dist/$(BINARY) $(GEN)/dist/$(BINARY)-arm64 $(GEN)/dist/$(BINARY)-x86_64
	codesign --sign - --force $(GEN)/dist/$(BINARY)
	COPYFILE_DISABLE=1 tar -czf $(DIST_ARCHIVE) -C $(GEN)/dist $(BINARY)
	@echo "→ $(DIST_ARCHIVE) ($(VERSION), $$(lipo -archs $(GEN)/dist/$(BINARY)))"

FORCE:

clean:
	rm -rf $(BINARY) .build $(GEN) dist

# Unit tests for peer-logic.swift + update-logic.swift (Swift Testing). SwiftPM is used for tests only.
test:
	swift test

install: build
	@# Under sudo the build above ran as root; hand the local binary back so a plain `make` can rebuild it.
	-chown -R $(LOCAL_USER) $(BINARY) $(GEN)
	install -d $(PREFIX)/bin
	install -m 755 $(BINARY) $(BINPATH)
	codesign --sign - --force $(BINPATH)
	mkdir -p "$(USER_HOME)/Library/LaunchAgents"
	sed \
		-e 's|DISPLAY_TOGGLE_LABEL|$(LABEL)|g' \
		-e 's|DISPLAY_TOGGLE_BINPATH|$(BINPATH)|g' \
		launch-agent.plist > "$(PLIST)"
	chown $(LOCAL_USER) "$(PLIST)"
	@# (Re)load under launchd so a re-install stops the old instance and runs the
	@# new binary now (RunAtLoad) — the launchd analogue of Windows' stop+relaunch.
	@# bootout is async — wait for the old job to fully leave the domain before
	@# bootstrapping, otherwise bootstrap races the teardown and fails with
	@# "Bootstrap failed: 5: Input/output error" (label still half-registered).
	-launchctl bootout gui/$(UID)/$(LABEL) 2>/dev/null; true
	@i=0; while launchctl print gui/$(UID)/$(LABEL) >/dev/null 2>&1 && [ $$i -lt 50 ]; do sleep 0.1; i=$$((i+1)); done
	launchctl bootstrap gui/$(UID) "$(PLIST)"
	@echo "→ Installed to $(BINPATH)"
	@echo "→ Registered for login autostart (running now)"

uninstall:
	-launchctl bootout gui/$(UID)/$(LABEL) 2>/dev/null
	pkill -x $(BINARY) 2>/dev/null; true
	rm -f "$(PLIST)" "$(BINPATH)"
	@echo "→ Uninstalled"

run: build
	pkill -x $(BINARY) 2>/dev/null; true
	nohup ./$(BINARY) >/dev/null 2>&1 &
