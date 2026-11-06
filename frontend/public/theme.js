{
  let theme = null
  try {
    theme = localStorage.getItem("ui-theme")
  } catch {
    // A privacy mode can refuse the storage. Then the theme follows the OS.
  }
  const isDark = theme === "dark" || (theme !== "light" && matchMedia("(prefers-color-scheme: dark)").matches)
  document.documentElement.classList.add(isDark ? "dark" : "light")
}
