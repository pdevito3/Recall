{
  let theme = null
  try {
    theme = localStorage.getItem("ui-theme")
  } catch {
    // A privacy mode can refuse the storage. Then the theme follows the OS.
  }
  if (theme) document.cookie = `ui-theme=${theme}; Path=/; Max-Age=31536000; SameSite=Lax`
  const isDark = theme === "dark" || (theme !== "light" && matchMedia("(prefers-color-scheme: dark)").matches)
  document.documentElement.classList.add(isDark ? "dark" : "light")
}
