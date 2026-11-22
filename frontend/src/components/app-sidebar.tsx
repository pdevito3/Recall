import {
  AiBrain01Icon,
  Book02Icon,
  InformationCircleIcon,
  Key01Icon,
  Login01Icon,
  Mic01Icon,
  Note01Icon,
  Settings01Icon,
  TextCheckIcon,
  UserGroupIcon,
  UserMultipleIcon,
} from "@hugeicons/core-free-icons"
import { HugeiconsIcon } from "@hugeicons/react"

import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupContent,
  SidebarGroupLabel,
  SidebarHeader,
  SidebarMenu,
  SidebarMenuItem,
  SidebarMenuLink,
  SidebarRail,
} from "@/components/ui/sidebar"
import { useSidebar } from "@/components/ui/use-sidebar"
import { ThemeToggle } from "@/components/theme-toggle"
import { useCurrentNavItem } from "@/components/current-nav-item"
import { UserMenu } from "@/domain/auth/components/user-menu"
import { useCanManageModelProviders, useCanManageUserAccess, useMembership } from "@/domain/membership/apis/get-membership"

const appName = "Recall"

const navItems = [
  {
    title: "Journal",
    url: "/",
    icon: Book02Icon,
  },
  {
    title: "Summaries",
    url: "/summaries",
    icon: Note01Icon,
  },
  {
    title: "People",
    url: "/people",
    icon: UserMultipleIcon,
  },
  {
    title: "Corrections",
    url: "/corrections",
    icon: TextCheckIcon,
  },
  {
    title: "Settings",
    url: "/settings",
    icon: Settings01Icon,
  },
  {
    title: "Personal access tokens",
    url: "/settings/personal-access-tokens",
    icon: Key01Icon,
  },
  {
    title: "Users",
    url: "/users",
    icon: UserGroupIcon,
  },
  {
    title: "Login providers",
    url: "/login-providers",
    icon: Login01Icon,
  },
  {
    title: "Model providers",
    url: "/model-providers",
    icon: AiBrain01Icon,
  },
  {
    title: "Transcription",
    url: "/transcription-engine",
    icon: Mic01Icon,
  },
  {
    title: "About",
    url: "/about",
    icon: InformationCircleIcon,
  },
] as const

const navUrls = [
  ...navItems.map((item) => item.url),
]

export function AppSidebar() {
  const current = useCurrentNavItem(navUrls)
  const { setOpenMobile } = useSidebar()
  const membership = useMembership()
  const canReadUsers = membership.status === "member" && membership.user.permissions.includes("read_users")
  const canManageModelProviders = useCanManageModelProviders()
  const canManageUserAccess = useCanManageUserAccess()

  // On mobile the sidebar is a sheet over the page. Close it when a link is
  // pressed, so the user sees the new page. On desktop the sheet is not used.
  const closeMobile = () => setOpenMobile(false)

  return (
    <Sidebar variant="inset" collapsible="icon">
      <SidebarHeader>
        <SidebarMenu>
          <SidebarMenuItem>
            <SidebarMenuLink size="lg" to="/" onPress={closeMobile}>
              {/* The name beside it labels the link, so the logo has no alt text of its own. */}
              <img src="/favicon.png?v=3" alt="" width={32} height={32} className="size-8 shrink-0" />
              <div className="grid flex-1 text-left text-sm leading-tight">
                <span className="line-clamp-2 font-semibold group-data-[collapsible=icon]:sr-only">{appName}</span>
              </div>
            </SidebarMenuLink>
          </SidebarMenuItem>
        </SidebarMenu>
      </SidebarHeader>
      <SidebarContent>
        <nav aria-label="Main">
          <SidebarGroup>
            <SidebarGroupLabel>Navigation</SidebarGroupLabel>
            <SidebarGroupContent>
              <SidebarMenu>
                {navItems.map((item) => {
                  if (item.url === "/users" && !canReadUsers) return null
                  if (item.url === "/model-providers" && !canManageModelProviders) return null
                  if (item.url === "/login-providers" && !canManageUserAccess) return null
                  return (
                    <SidebarMenuItem key={item.title}>
                      <SidebarMenuLink
                        to={item.url}
                        current={current?.url === item.url ? current.current : undefined}
                        tooltip={item.title}
                        onPress={closeMobile}
                      >
                        <HugeiconsIcon icon={item.icon} />
                        <span>{item.title}</span>
                      </SidebarMenuLink>
                    </SidebarMenuItem>
                  )
                })}
              </SidebarMenu>
            </SidebarGroupContent>
          </SidebarGroup>
        </nav>
      </SidebarContent>
      <SidebarFooter>
        <SidebarMenu>
          <SidebarMenuItem>
            <UserMenu />
          </SidebarMenuItem>
          <SidebarMenuItem>
            <div className="flex items-center justify-center p-2 group-data-[collapsible=icon]:p-0">
              <ThemeToggle />
            </div>
          </SidebarMenuItem>
        </SidebarMenu>
      </SidebarFooter>
      <SidebarRail />
    </Sidebar>
  )
}
