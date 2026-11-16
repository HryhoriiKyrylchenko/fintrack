# FinTrack UI design guide

Place this file at `docs/DESIGN.md`. It defines the look and behaviour of the front end. Components never hard-code colours, sizes or fonts; they use the theme tokens below.

## 1. Principles

1. **Numbers first.** This is a tool for reading money. Amounts are the most prominent element on every screen; decoration is minimal.
2. **Calm, not flashy.** One accent colour. Colour is used to carry meaning (state, gain or loss, category), never for decoration.
3. **Light and dark from day one.** Every colour is a token with a light and a dark value. The user can switch; the default follows the system setting.
4. **Mobile first.** Every screen works at 360 px width and scales up. The app is meant to be installable as a PWA later.
5. **Never colour alone.** Gain and loss, warnings and states always have a sign, icon or label in addition to colour.
6. **Confirm before writing.** Anything that writes to the journal shows a clear summary first (this matches the draft-and-confirm flow in the specification).

## 2. Component library

- **MudBlazor** (MIT). Use its components, grid, theming and icons (Material icons). Do not add a second component library.
- Charts: MudBlazor charts first. If they are not enough for portfolio history, propose one chart library and ask before adding it.
- Custom CSS is the exception. When needed, it uses the CSS variables that MudBlazor generates from the theme.

## 3. Theme: "Indigo" (default)

Define these in one `MudTheme` (`PaletteLight` and `PaletteDark`). Chosen because indigo does not collide with green and red, which are reserved for gain and loss.

| Token | Light | Dark | Use |
|---|---|---|---|
| Primary | `#4F46E5` | `#818CF8` | Main actions, links, selected state, progress |
| Background | `#F8FAFC` | `#0B1020` | Page |
| Surface | `#FFFFFF` | `#151B2E` | Cards, tables, dialogs |
| Surface variant | `#F1F5F9` | `#1E2640` | Table header, hover, inputs |
| Text primary | `#0F172A` | `#E5E7EB` | Main text and amounts |
| Text secondary | `#64748B` | `#94A3B8` | Labels, captions |
| Divider | `#E2E8F0` | `#273049` | Borders, separators |
| Success / gain | `#16A34A` | `#4ADE80` | Positive result, goal reached |
| Error / loss | `#DC2626` | `#F87171` | Negative result, rejected operation |
| Warning | `#D97706` | `#FBBF24` | Shortfall, limit close, stale price |
| Info | `#0284C7` | `#38BDF8` | Neutral hints, drafts |

Rules:

- Primary is the only brand colour. Do not introduce a secondary brand colour. In MudBlazor's palette, `Secondary` and `Tertiary` are set to the text-secondary grey, so `Color.Secondary` means muted text, never a second accent.
- The tokens live in one file, `src/Web/FinTrack.Web/Theme/FinTrackTheme.cs`; `wwwroot/css/app.css` uses only MudBlazor's CSS variables (`var(--mud-palette-...)`).
- Check text contrast (WCAG AA, 4.5:1) when a token changes.
- Semantic colours mean one thing each: success = gain or done, error = loss or failure, warning = needs attention, info = neutral notice.

## 4. Category colours (buckets, sections, chart series)

A fixed, colour-blind-safe set, assigned in this order and reused consistently for the same bucket or section:

`#0072B2`, `#E69F00`, `#009E73`, `#CC79A7`, `#56B4E9`, `#D55E00`, `#F0E442`, `#999999`

Beyond eight series, group the rest as "Other". The system items `Unallocated` and `Pending` always use the grey `#999999`.

## 5. Typography

- Font: **Inter** for everything, with the system sans-serif stack as fallback.
- All amounts, quantities and percentages use tabular figures (`font-variant-numeric: tabular-nums`) so digits align in columns.
- Scale: page title 24, section title 18, body 14 to 16, caption 12. Two weights only: 400 and 600.
- Key figures on cards (balance, portfolio value) are 24 to 28 px, weight 600.

## 6. Numbers and money

- Always show the currency code or symbol next to an amount. Never show a bare number where currency could be unclear.
- Amounts use the precision of their currency (`Currency.DecimalPlaces`: PLN, EUR and USD have 2, some currencies 0 or 3); input with more decimals is rejected, never rounded. Crypto quantities: up to 8 significant decimals, trailing zeros trimmed. Percentages: 1 or 2 decimals.
- Thousands separators follow the user's locale setting.
- Gain and loss: explicit sign and an arrow icon plus colour (`+2.4% ▲` in success, `−1.1% ▼` in error). Use the real minus sign.
- Amounts in tables are right-aligned; labels are left-aligned.
- A converted total shows its reporting currency and the rate date in a caption or tooltip.
- Numbers, dates and times are formatted by the owner's locale setting, and times are shown in the owner's time zone, both from Settings (not the browser's).
- A stale or manually overridden price is marked with a warning icon and a tooltip.

## 7. Language

- English only for now. Every visible string comes from resource files, so other languages can be added later without code changes.
- No text built by joining fragments; use full sentences with placeholders.
- Leave room in layouts: translated text is often 30% longer.

## 8. Layout

- Spacing on an 8 px grid (4 px for dense table cells).
- Navigation: left drawer on desktop, bottom navigation on mobile. Top-level items: Overview, Buckets, Plans, Loans, Investments, Journal, Settings; an item appears once its page exists. Overview is the start page; until it exists, the start page opens Settings. The system status page (does the app reach its server and database) sits under Settings (`/settings/status`), and Settings stays highlighted there.
- First run: until the settings are saved, the app shows only the setup screen, without navigation; when the server cannot be reached at start, it shows why and a retry.
- Overview shows: totals in the reporting currency, a card per bucket (available, progress to limit, due by plan, open loans), and the items that need attention.
- Cards for summaries, tables for journals and lists. On mobile a wide table turns into a list of cards instead of scrolling sideways.
- Corner radius 8 px on controls, 12 px on cards. Flat surfaces: elevation only for dialogs and menus.
- Forms: one column, label above the field, validation message under the field, primary action at the bottom right (full width on mobile).

## 9. States and feedback

- Every list has an empty state that says what the area is for and offers the first action.
- Loading uses skeletons for cards and tables, not full-page spinners.
- A rejected operation explains why in plain words (for example "This would make the bucket balance negative") and what to do next.
- Destructive or irreversible actions (waive a shortfall, reversal entry, archive) need a confirmation dialog that names the object and the amount.
- Success feedback is a short snackbar; errors stay visible until dismissed.

## 10. Accessibility

- Full keyboard navigation with a visible focus ring.
- Touch targets at least 44 px.
- Text contrast WCAG AA.
- Charts have a text or table alternative for the same numbers.

## 11. Alternatives considered

- **Navy**: primary `#1D4ED8` (dark `#60A5FA`). More conservative, banking feel.
- **Graphite and amber**: dark-first, primary `#F59E0B`. Trading-terminal feel, but amber then conflicts with the warning colour.

Switching theme means changing the tokens in section 3 only.

## 12. Charts and reports

What to show is listed in `docs/ARCHITECTURE.md` section 13. How to show it:

- Line charts for values over time, bars for per-month amounts, a donut or a horizontal stacked bar for allocation (at most eight slices, the rest as "Other").
- Portfolio value and net invested share one chart: value as a solid line in the primary colour, net invested as a dashed neutral line.
- Category colours come from section 4 and stay the same for a bucket or section on every chart.
- The Y axis starts at zero for bars. Line charts may zoom, but then the axis range is clearly labelled.
- Missing data is a visible gap, never a straight line across it.
- Every chart has a period selector where time is involved, a tooltip with exact figures and currency, and a "show as table" switch.
- On mobile a chart takes the full width and its legend moves below it.
- No decorative charts: each chart answers one question, stated in its title ("Net worth over time").
