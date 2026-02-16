# Aircraft Integration

The package provides the instrument implementation, but an aircraft still needs to reference the HTML gauge.

## Example panel.cfg snippet
Adjust path/index/size for your target panel:

```ini
[VcockpitXX]
size_mm=1024,1024
pixel_size=1024,1024
texture=$OpensquawkBridge
htmlgauge00=OpenSquawkBridge/OpenSquawkBridge.html,0,0,1024,1024
```

## Notes
- The gauge path must match the folder and file names in this package.
- If your aircraft uses a custom panel architecture, map this gauge into the correct VCockpit section.
- Keep the instrument visible while validating startup/login/telemetry behavior.

## Registration ID
The instrument registers with:
- `registerInstrument("opensquawk-bridge", ...)`

If your panel flow requires explicit instrument IDs, use that name consistently.
