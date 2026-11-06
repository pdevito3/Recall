// Sets the theme class before the first paint, so that a dark page does not show a light frame while the app loads.
// It must use the storage key and the "system" rule of ThemeProvider (src/components/theme-provider.tsx).
// It is a file and not an inline script, because the Content-Security-Policy of the Server allows only scripts of this origin.
{
  let theme = null
  try {
    theme = localStorage.getItem("ui-theme")
  } catch {
    // A sandboxed frame or a privacy mode can refuse the storage. Then the theme follows the OS.
  }
  const isDark = theme === "dark" || (theme !== "light" && matchMedia("(prefers-color-scheme: dark)").matches)
  document.documentElement.classList.add(isDark ? "dark" : "light")
}
