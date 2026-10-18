// Desktop stub for .NET MAUI's HybridWebView shim.
//
// Every page loads `_framework/hybridwebview.js` so the same markup works on
// mobile, where the MAUI control serves the real script from this path. On the
// desktop that path does not exist, and a missing <script> would be a 404 in the
// web view's console — so the desktop ships this empty file instead. The bridge
// in hostbridge.js feature-detects `window.HybridWebView` and falls back to the
// Photino shim, which is what this file deliberately does not define.
