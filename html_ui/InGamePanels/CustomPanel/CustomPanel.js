class IngamePanelOpenSquawkBridge extends TemplateElement {
    constructor() {
        super();
        this.runtime = null;
        this.ingameUi = null;
        this.updateTimer = null;
        this.runtimeRetryTimer = null;
        this.panelActive = false;
    }

    connectedCallback() {
        super.connectedCallback();

        this.ingameUi = this.querySelector("ingame-ui");
        this.bindPanelEvents();
        this.bindCollapsibleSections();
        this.ensureRuntime("connectedCallback");
    }

    disconnectedCallback() {
        if (typeof super.disconnectedCallback === "function") {
            super.disconnectedCallback();
        }

        if (this.updateTimer) {
            clearInterval(this.updateTimer);
            this.updateTimer = null;
        }

        if (this.runtimeRetryTimer) {
            clearInterval(this.runtimeRetryTimer);
            this.runtimeRetryTimer = null;
        }

        if (this.runtime) {
            this.runtime.dispose();
            this.runtime = null;
        }
    }

    bindPanelEvents() {
        if (!this.ingameUi) {
            return;
        }

        this.ingameUi.addEventListener("panelActive", () => {
            this.panelActive = true;
            this.log("info", "panel.active", "Toolbar panel became active");
            this.ensureRuntime("panelActive");
        });

        this.ingameUi.addEventListener("panelInactive", () => {
            this.panelActive = false;
            this.log("info", "panel.inactive", "Toolbar panel became inactive");
        });

        this.ingameUi.addEventListener("onResizeElement", () => {
            this.log("debug", "panel.resize", "Toolbar panel resized");
        });
    }

    bindCollapsibleSections() {
        var panels = this.querySelectorAll(".osb-panel-collapsible h2");
        for (var i = 0; i < panels.length; i++) {
            panels[i].addEventListener("click", function (e) {
                var section = e.currentTarget.closest(".osb-panel-collapsible");
                if (section) {
                    section.classList.toggle("osb-collapsed");
                }
            });
        }
    }

    ensureRuntime(reason) {
        if (this.runtime) {
            return;
        }

        if (typeof OpenSquawkBridgeRuntime !== "function") {
            this.log("warn", "runtime.unavailable", "OpenSquawkBridgeRuntime is not available yet", {
                reason: reason
            });

            if (!this.runtimeRetryTimer) {
                this.runtimeRetryTimer = setInterval(() => {
                    if (typeof OpenSquawkBridgeRuntime === "function") {
                        clearInterval(this.runtimeRetryTimer);
                        this.runtimeRetryTimer = null;
                        this.ensureRuntime("delayed-runtime-available");
                    }
                }, 1000);
            }

            return;
        }

        try {
            this.runtime = new OpenSquawkBridgeRuntime(this);
            this.log("info", "runtime.created", "OpenSquawk bridge runtime created", {
                reason: reason
            });
        } catch (error) {
            this.log("error", "runtime.create_failed", "Failed to create OpenSquawk bridge runtime", {
                reason: reason,
                error: String(error && error.message ? error.message : error)
            });
            return;
        }

        if (!this.updateTimer) {
            this.updateTimer = setInterval(() => {
                if (!this.runtime) {
                    return;
                }

                try {
                    this.runtime.update(Date.now());
                } catch (error) {
                    this.log("error", "runtime.update_failed", "Runtime update tick failed", {
                        error: String(error && error.message ? error.message : error)
                    });
                }
            }, 250);
        }
    }

    log(level, event, message, details) {
        var payload = "[OpenSquawkToolbarPanel][" + level + "][" + event + "] " + message;
        if (typeof console === "undefined" || typeof console.log !== "function") {
            return;
        }

        if (details) {
            console.log(payload, details);
        } else {
            console.log(payload);
        }
    }
}

if (typeof window !== "undefined" && window.customElements) {
    if (!window.customElements.get("ingamepanel-opensquawk-bridge")) {
        window.customElements.define("ingamepanel-opensquawk-bridge", IngamePanelOpenSquawkBridge);
    }
}

if (typeof checkAutoload === "function") {
    checkAutoload();
}
