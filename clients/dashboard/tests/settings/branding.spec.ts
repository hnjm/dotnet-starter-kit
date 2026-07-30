import { expect, test, type Page } from "@playwright/test";
import { installShellMocks } from "../helpers/shell-mocks";
import { mockJsonResponse, mockProblemDetails } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";

// A full TenantThemeDto the GET /theme mock returns. Palette values are the
// framework light/dark defaults; typography + layout are inert here (the
// editor omits them) but must be present so the DTO round-trips.
const THEME = {
  lightPalette: {
    primary: "#2563EB", secondary: "#0F172A", tertiary: "#6366F1",
    background: "#F8FAFC", surface: "#FFFFFF", error: "#DC2626",
    warning: "#F59E0B", success: "#16A34A", info: "#0284C7",
  },
  darkPalette: {
    primary: "#38BDF8", secondary: "#94A3B8", tertiary: "#818CF8",
    background: "#0B1220", surface: "#111827", error: "#F87171",
    warning: "#FBBF24", success: "#22C55E", info: "#38BDF8",
  },
  brandAssets: { logoUrl: null, logoDarkUrl: null, faviconUrl: null },
  typography: { fontFamily: "Inter", headingFontFamily: "Inter", fontSizeBase: 16, lineHeightBase: 1.5 },
  layout: { borderRadius: "0.5rem", defaultElevation: 1 },
  isDefault: true,
} as const;

/** Grant the branding permissions the tab + endpoints gate on. */
async function grantBrandingPerms(page: Page) {
  await mockJsonResponse(page, "**/api/v1/identity/permissions", [
    "Permissions.Tenants.ViewTheme",
    "Permissions.Tenants.UpdateTheme",
  ]);
}

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, TEST_USER);
  await installShellMocks(page);
});

test.describe("settings/branding — tenant theme editor", () => {
  test("renders the editor and saves an edited palette via PUT", async ({ page }) => {
    await grantBrandingPerms(page);
    await mockJsonResponse(page, "**/api/v1/tenants/theme", THEME);

    // Capture the PUT body while answering it 204.
    let putBody: unknown = null;
    await page.route("**/api/v1/tenants/theme", async (route) => {
      if (route.request().method() !== "PUT") return route.fallback();
      putBody = route.request().postDataJSON();
      await route.fulfill({ status: 204, body: "" });
    });

    await page.goto("/settings/branding");

    // "Branding" appears in the header, the nav tab, and the section title —
    // target the section's heading specifically to stay unambiguous.
    await expect(page.getByRole("heading", { name: "Branding", exact: true })).toBeVisible();
    await expect(page.getByText("Light palette")).toBeVisible();
    await expect(page.getByText("Dark palette")).toBeVisible();

    // The hex text input sits next to the color chip; target it by its value.
    // Editing it flips the section to "unsaved" and enables Save.
    const primaryHex = page.locator('input[value="#2563EB"]').first();
    await primaryHex.fill("#123ABC");
    await expect(page.getByText("unsaved")).toBeVisible();

    await page.getByRole("button", { name: "Save branding" }).click();

    await expect(page.getByText("Branding saved")).toBeVisible();
    expect(putBody).not.toBeNull();
    expect((putBody as { lightPalette: { primary: string } }).lightPalette.primary).toBe("#123ABC");
  });

  test("reset posts to /theme/reset", async ({ page }) => {
    await grantBrandingPerms(page);
    await mockJsonResponse(page, "**/api/v1/tenants/theme", THEME);

    let resetCalled = false;
    await page.route("**/api/v1/tenants/theme/reset", async (route) => {
      resetCalled = true;
      await route.fulfill({ status: 204, body: "" });
    });

    await page.goto("/settings/branding");
    await page.getByRole("button", { name: "Reset branding to defaults" }).click();

    await expect(page.getByText("Branding reset to defaults")).toBeVisible();
    expect(resetCalled).toBe(true);
  });

  test("hides the Branding tab for users without UpdateTheme", async ({ page }) => {
    // Default permissions ([]) — no re-mock. Land on another settings tab.
    await page.goto("/settings/profile");

    await expect(page.getByRole("link", { name: /Profile/ })).toBeVisible();
    await expect(page.getByRole("link", { name: /Branding/ })).toHaveCount(0);
  });

  test("shows an error toast and keeps the draft when PUT is rejected 403", async ({ page }) => {
    await grantBrandingPerms(page);
    await mockJsonResponse(page, "**/api/v1/tenants/theme", THEME);
    // 403 only on the PUT; GET must still succeed so the editor renders.
    await page.route("**/api/v1/tenants/theme", async (route) => {
      if (route.request().method() !== "PUT") return route.fallback();
      await route.fulfill({
        status: 403,
        headers: { "Content-Type": "application/problem+json" },
        body: JSON.stringify({
          type: "https://httpstatuses.io/403",
          title: "Forbidden",
          status: 403,
          detail: "You lack permission to update the theme.",
        }),
      });
    });

    await page.goto("/settings/branding");
    const primaryHex = page.locator('input[value="#2563EB"]').first();
    await primaryHex.fill("#123ABC");
    await page.getByRole("button", { name: "Save branding" }).click();

    await expect(page.getByText("Save failed")).toBeVisible();
    // Draft preserved — the edited value is still in the input.
    await expect(page.locator('input[value="#123ABC"]').first()).toBeVisible();
  });

  test("shows an error band when the theme fails to load", async ({ page }) => {
    await grantBrandingPerms(page);
    await mockProblemDetails(page, "**/api/v1/tenants/theme", 500, {
      title: "Server error",
      detail: "Could not load theme.",
    });

    await page.goto("/settings/branding");

    // The GET retries twice (query-client retry policy) before the error state
    // lands, so allow past the default 5s window.
    await expect(page.getByText(/Failure/)).toBeVisible({ timeout: 12_000 });
    await expect(page.getByText("Could not load theme.")).toBeVisible();
  });
});
