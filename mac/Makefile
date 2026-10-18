BINARY     = display-toggle
SOURCES    = display-toggle.swift peer-logic.swift peer-link.swift
PREFIX    ?= /usr/local
BINPATH    = $(PREFIX)/bin/$(BINARY)
LOCAL_USER := $(or $(SUDO_USER),$(shell whoami))
UID        := $(shell id -u $(LOCAL_USER))
USER_HOME  := $(shell eval echo ~$(LOCAL_USER))
LABEL      = com.$(LOCAL_USER).display-toggle
PLIST      = $(USER_HOME)/Library/LaunchAgents/$(LABEL).plist

.PHONY: all build clean install uninstall run test

all: build

build: $(BINARY)

# Info.plist is embedded (no .app bundle) so macOS sees the Bonjour / Local Network declarations.
$(BINARY): $(SOURCES) Info.plist
	swiftc $(SOURCES) -o $(BINARY) -Xlinker -sectcreate -Xlinker __TEXT -Xlinker __info_plist -Xlinker Info.plist

clean:
	rm -rf $(BINARY) .build

# Unit tests for peer-logic.swift (Swift Testing). SwiftPM is used for tests only.
test:
	swift test

install: build
	@# Under sudo the build above ran as root; hand the local binary back so a plain `make` can rebuild it.
	-chown $(LOCAL_USER) $(BINARY)
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
