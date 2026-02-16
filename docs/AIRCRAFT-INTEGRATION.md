# Aircraft Integration (Not Required)

The current implementation is a global MSFS toolbar panel. You do not need to edit any aircraft `panel.cfg`.

## Expected usage
1. Install package into `Community`.
2. Start MSFS.
3. Open the OpenSquawk toolbar panel.

## If the toolbar panel does not appear
- Confirm these files exist in the installed package:
  - `InGamePanels/maximus-ingamepanels-custom.spb`
  - `html_ui/InGamePanels/CustomPanel/CustomPanel.html`
  - `html_ui/InGamePanels/CustomPanel/CustomPanel.js`
  - `html_ui/Textures/Menu/toolbar/ICON_TOOLBAR_MAXIMUS_CUSTOM_PANEL.svg`
- Rebuild `layout.json` after any file changes (`npm run update:layout`).
- Restart MSFS after replacing the package.

## Legacy fallback only
The VCockpit instrument files are still present for compatibility/testing, but they are no longer the primary integration path.
