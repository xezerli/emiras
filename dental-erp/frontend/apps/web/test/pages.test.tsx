import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { fakeApi, json, mockAuth, path, renderWithQuery, type Handler } from "./support";

const replace = vi.fn();
const push = vi.fn();
let routeParams: Record<string, string> = {};
let handler: Handler = () => json(404, {});
let perms: string[] = [];
let calls: Request[] = [];
let shared: ReturnType<typeof fakeApi> | undefined;

vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace, push }),
  usePathname: () => "/",
  useParams: () => routeParams,
}));
vi.mock("next/link", () => ({ default: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => <a href={href} {...rest}>{children}</a> }));
vi.mock("@/shared/AuthProvider", () => ({ useAuth: () => mockAuth(perms).useAuth() }));
vi.mock("@/shared/useApi", async () => {
  const actual = await vi.importActual<typeof import("@/shared/useApi")>("@/shared/useApi");
  return {
    ...actual,
    // Bir test = bir klient: render-lər arasında çağırış jurnalı itməsin
    useApi: () => {
      shared ??= fakeApi((r) => handler(r));
      calls = shared.calls;
      return shared.client;
    },
  };
});
vi.mock("@/shared/session", () => ({ login: vi.fn(), tokenStore: { get: () => "t", set: vi.fn() }, api: vi.fn() }));

beforeEach(() => {
  replace.mockReset();
  push.mockReset();
  routeParams = {};
  calls = [];
  shared = undefined;
  perms = [];
});

describe("login page", async () => {
  const { default: LoginPage } = await import("@/app/login/page");
  const session = await import("@/shared/session");

  it("keeps the button disabled until both fields are filled", async () => {
    renderWithQuery(<LoginPage />);
    const button = screen.getByRole("button", { name: "Daxil ol" });
    expect(button).toBeDisabled();
    await userEvent.type(screen.getByLabelText("E-poçt"), "a@b.az");
    expect(button).toBeDisabled();
    await userEvent.type(screen.getByLabelText("Parol"), "x");
    expect(button).toBeEnabled();
  });

  it("navigates to the dashboard on success", async () => {
    vi.mocked(session.login).mockResolvedValue({ kind: "ok" });
    renderWithQuery(<LoginPage />);
    await userEvent.type(screen.getByLabelText("E-poçt"), "a@b.az");
    await userEvent.type(screen.getByLabelText("Parol"), "secret");
    await userEvent.click(screen.getByRole("button", { name: "Daxil ol" }));
    await waitFor(() => expect(replace).toHaveBeenCalledWith("/dashboard"));
  });

  it("shows a mapped error and re-enables the form on failure", async () => {
    const { ApiError } = await import("@dentacore/api-client");
    vi.mocked(session.login).mockRejectedValue(new ApiError(401, "auth.account_locked", "x", "internal detail"));
    renderWithQuery(<LoginPage />);
    await userEvent.type(screen.getByLabelText("E-poçt"), "a@b.az");
    await userEvent.type(screen.getByLabelText("Parol"), "secret");
    await userEvent.click(screen.getByRole("button", { name: "Daxil ol" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("bloklanıb");
    expect(screen.queryByText(/internal detail/)).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Daxil ol" })).toBeEnabled();
  });

  it("explains the 2FA challenge instead of silently failing", async () => {
    vi.mocked(session.login).mockResolvedValue({ kind: "challenge", challengeId: "c", methods: ["totp"] });
    renderWithQuery(<LoginPage />);
    await userEvent.type(screen.getByLabelText("E-poçt"), "a@b.az");
    await userEvent.type(screen.getByLabelText("Parol"), "secret");
    await userEvent.click(screen.getByRole("button", { name: "Daxil ol" }));
    expect(await screen.findByRole("status")).toHaveTextContent("ikinci mərhələ");
  });
});

describe("invoice payment page", async () => {
  const { default: InvoicePage } = await import("@/app/(app)/billing/[id]/page");
  const invoice = (over: object = {}) => ({
    id: "inv1", number: "INV-1", status: "issued", total: 80, paidTotal: 0, balance: 80, items: [{ id: "i", description: "Plomb", quantity: 1, lineTotal: 80 }], payments: [], ...over,
  });

  it("reuses the SAME Idempotency-Key when a failed payment is retried, then rotates it after success", async () => {
    perms = ["invoice:read", "payment:write"];
    routeParams = { id: "inv1" };
    let attempt = 0;
    handler = (r) => {
      if (r.method === "GET") return json(200, invoice());
      attempt++;
      return attempt === 1 ? json(503, { code: "http.503" }) : json(201, { id: "p" });
    };
    renderWithQuery(<InvoicePage />);
    await screen.findByRole("heading", { level: 1 });
    await userEvent.type(screen.getByLabelText("Məbləğ (AZN)"), "30");
    await userEvent.click(screen.getByRole("button", { name: "Qəbul et" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("təkrar göndərmək təhlükəsizdir");
    await userEvent.click(screen.getByRole("button", { name: "Qəbul et" }));
    await screen.findByText("Ödəniş qeydə alındı");

    const posts = calls.filter((c) => c.method === "POST");
    expect(posts).toHaveLength(2);
    expect(posts[0]!.headers.get("idempotency-key")).toBe(posts[1]!.headers.get("idempotency-key"));
    expect(path(posts[0]!)).toBe("/invoices/inv1/payments");

    await userEvent.type(screen.getByLabelText("Məbləğ (AZN)"), "10");
    await userEvent.click(screen.getByRole("button", { name: "Qəbul et" }));
    await waitFor(() => expect(calls.filter((c) => c.method === "POST")).toHaveLength(3));
    expect(calls.filter((c) => c.method === "POST")[2]!.headers.get("idempotency-key")).not.toBe(posts[0]!.headers.get("idempotency-key"));
  });

  it("maps the server's shift error to guidance and never posts an overpayment", async () => {
    perms = ["invoice:read", "payment:write"];
    routeParams = { id: "inv1" };
    handler = (r) => (r.method === "GET" ? json(200, invoice()) : json(422, { code: "billing.shift_required" }));
    renderWithQuery(<InvoicePage />);
    await screen.findByRole("heading", { level: 1 });

    await userEvent.type(screen.getByLabelText("Məbləğ (AZN)"), "999");
    await userEvent.click(screen.getByRole("button", { name: "Qəbul et" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("qalıq borcdan çoxdur");
    expect(calls.filter((c) => c.method === "POST")).toHaveLength(0);

    await userEvent.clear(screen.getByLabelText("Məbləğ (AZN)"));
    await userEvent.type(screen.getByLabelText("Məbləğ (AZN)"), "10");
    await userEvent.click(screen.getByRole("button", { name: "Qəbul et" }));
    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("kassa smenini açın"));
  });

  it("shows payment methods in Azerbaijani, not the raw API value", async () => {
    perms = ["invoice:read"];
    routeParams = { id: "inv1" };
    handler = () => json(200, invoice({ status: "partially_paid", balance: 50, paidTotal: 30, payments: [{ id: "p", kind: "payment", method: "cash", amount: 30 }, { id: "q", kind: "payment", method: "pos", amount: 5 }] }));
    renderWithQuery(<InvoicePage />);
    expect(await screen.findByText(/\(Nağd\)/)).toBeInTheDocument();
    expect(screen.getByText(/\(POS terminal\)/)).toBeInTheDocument();
    expect(screen.queryByText(/\(cash\)/)).not.toBeInTheDocument();
  });

  it("hides the payment form without payment:write or on a paid invoice", async () => {
    routeParams = { id: "inv1" };
    perms = ["invoice:read"];
    handler = () => json(200, invoice());
    const first = renderWithQuery(<InvoicePage />);
    await screen.findByRole("heading", { level: 1 });
    expect(screen.queryByRole("button", { name: "Qəbul et" })).not.toBeInTheDocument();
    first.unmount();

    perms = ["invoice:read", "payment:write"];
    handler = () => json(200, invoice({ status: "paid", balance: 0, paidTotal: 80 }));
    renderWithQuery(<InvoicePage />);
    await screen.findByRole("heading", { level: 1 });
    expect(screen.queryByRole("button", { name: "Qəbul et" })).not.toBeInTheDocument();
  });
});

describe("new patient page", async () => {
  const { default: NewPatientPage } = await import("@/app/(app)/patients/new/page");

  async function fill() {
    await userEvent.type(screen.getByLabelText("Ad", { exact: true }), "Aysel");
    await userEvent.type(screen.getByLabelText("Soyad"), "Əliyeva");
    await userEvent.type(screen.getByLabelText("Telefon"), "+994 50 123-45-67");
  }

  it("normalises the phone, asks about duplicates and confirms with the same key", async () => {
    perms = ["patient:write"];
    let n = 0;
    handler = () => (++n === 1 ? json(409, { code: "patient.duplicate" }) : json(201, { id: "new-id" }));
    renderWithQuery(<NewPatientPage />);
    await fill();
    await userEvent.click(screen.getByRole("button", { name: "Saxla" }));
    const warning = await screen.findByRole("alert");
    expect(warning).toHaveTextContent("Oxşar pasiyent");

    await userEvent.click(screen.getByRole("button", { name: "Yenə də yarat" }));
    await waitFor(() => expect(push).toHaveBeenCalledWith("/patients/new-id"));

    const [first, second] = calls;
    expect(new URL(first!.url).searchParams.get("confirmDuplicate")).toBe("false");
    expect(new URL(second!.url).searchParams.get("confirmDuplicate")).toBe("true");
    expect(first!.headers.get("idempotency-key")).toBe(second!.headers.get("idempotency-key"));
    const sent = await first!.json();
    expect(sent.phone).toBe("+994501234567");
    expect(sent.branchId).toBe("b1");
  });

  it("requires first and last name", async () => {
    perms = ["patient:write"];
    renderWithQuery(<NewPatientPage />);
    expect(screen.getByRole("button", { name: "Saxla" })).toBeDisabled();
  });
});

describe("calendar page", async () => {
  const { default: CalendarPage } = await import("@/app/(app)/calendar/page");
  const appt = (id: string, status: string, name: string) => ({ id, status, patientName: name, start: "2026-10-13T09:00:00Z", end: "2026-10-13T09:30:00Z", reason: "x" });

  it("offers only the actions each status allows and refreshes after check-in", async () => {
    perms = ["appointment:read", "appointment:write"];
    let checkedIn = false;
    handler = (r) => {
      if (r.method === "POST") {
        checkedIn = true;
        return json(200, { id: "t" });
      }
      return json(200, [appt("a1", checkedIn ? "checked_in" : "booked", "Rəşad"), appt("a2", "completed", "Tamam"), appt("a3", "no_show", "Gəlməyən")]);
    };
    renderWithQuery(<CalendarPage />);
    expect(await screen.findByText("Planlaşdırılıb")).toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: /Gəldi|Gəlmədi:|Ləğv/ })).toHaveLength(3);   // yalnız a1

    await userEvent.click(screen.getByRole("button", { name: "Gəldi: Rəşad" }));
    expect(await screen.findByText("Gəlib")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Gəldi: Rəşad" })).not.toBeInTheDocument();
    expect(path(calls.find((c) => c.method === "POST")!)).toBe("/appointments/a1/check-in");
  });

  it("explains a rejected check-in with the mapped message", async () => {
    perms = ["appointment:read", "appointment:write"];
    handler = (r) => (r.method === "POST" ? json(409, { code: "appointment.checkin_window" }) : json(200, [appt("a1", "booked", "Rəşad")]));
    renderWithQuery(<CalendarPage />);
    await userEvent.click(await screen.findByRole("button", { name: "Gəldi: Rəşad" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Check-in vaxtı");
  });

  it("read-only users get no action column", async () => {
    perms = ["appointment:read"];
    handler = () => json(200, [appt("a1", "booked", "Rəşad")]);
    renderWithQuery(<CalendarPage />);
    await screen.findByText("Rəşad");
    expect(screen.queryByRole("columnheader", { name: "Əməliyyat" })).not.toBeInTheDocument();
  });
});

describe("patients list page", async () => {
  const { default: PatientsPage } = await import("@/app/(app)/patients/page");

  it("falls back to SQL when search is down and shows masked phones; write permission controls the create link", async () => {
    perms = ["patient:read"];
    handler = (r) =>
      path(r) === "/search/patients"
        ? json(503, { code: "search.unavailable" })
        : json(200, { items: [{ id: "p1", chartNo: 5, firstName: "Aysel", lastName: "Əliyeva", phoneMasked: "+994•••67" }], nextCursor: null });
    renderWithQuery(<PatientsPage />);
    expect(await screen.findByRole("link", { name: "Əliyeva Aysel" })).toHaveAttribute("href", "/patients/p1");
    expect(screen.getByText("+994•••67")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Yeni pasiyent" })).not.toBeInTheDocument();

    await userEvent.type(screen.getByLabelText("Axtar"), "Əl");
    await waitFor(() => expect(calls.some((c) => path(c) === "/search/patients")).toBe(true));
    await waitFor(() => expect(calls.filter((c) => path(c) === "/patients").length).toBeGreaterThan(1));
  });

  it("shows an empty state with guidance when nothing matches", async () => {
    perms = ["patient:read", "patient:write"];
    handler = () => json(200, { items: [], nextCursor: null });
    renderWithQuery(<PatientsPage />);
    expect(await screen.findByText("Pasiyent tapılmadı")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Yeni pasiyent" })).toBeInTheDocument();
  });
});

describe("patient card", async () => {
  const { default: PatientPage } = await import("@/app/(app)/patients/[id]/page");
  const patient = (reveal: boolean) => ({
    id: "p1", chartNo: 5, firstName: "Aysel", lastName: "Əliyeva", status: "active", noShowCount: 0, hasSevereAllergy: true,
    phone: reveal ? "+994501234567" : "+994•••67", email: reveal ? "a@b.az" : "a•••@b.az", nationalId: null,
  });

  it("warns about severe allergies and keeps data masked until revealed, then re-masks after 30 s", async () => {
    perms = ["patient:read", "patient:read_sensitive", "clinical:read"];
    routeParams = { id: "p1" };
    handler = (r) => {
      if (path(r).endsWith("/medical-profile")) return json(200, { allergies: [{ id: "a", substance: "Penisilin", severity: "anaphylaxis" }] });
      return json(200, patient(new URL(r.url).searchParams.get("reveal") === "true"));
    };
    renderWithQuery(<PatientPage />);
    expect(await screen.findByText("Ağır allergiya")).toBeInTheDocument();
    expect(screen.getAllByText(/Penisilin/).length).toBeGreaterThan(0);
    expect(screen.getByText("Anafilaksiya")).toBeInTheDocument();       // lokallaşdırılmış ağırlıq
    expect(screen.queryByText("anaphylaxis")).not.toBeInTheDocument();  // xam enum göstərilmir
    expect(screen.queryByText("+994501234567")).not.toBeInTheDocument();

    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      await userEvent.click(screen.getByRole("button", { name: "Məlumatları göstər" }));
      expect(await screen.findByText("+994501234567")).toBeInTheDocument();
      await vi.advanceTimersByTimeAsync(30_001);
      await waitFor(() => expect(screen.queryByText("+994501234567")).not.toBeInTheDocument());
      expect(screen.getByRole("button", { name: "Məlumatları göstər" })).toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it("shows no reveal control or medical profile without the permissions", async () => {
    perms = ["patient:read"];
    routeParams = { id: "p1" };
    handler = () => json(200, patient(false));
    renderWithQuery(<PatientPage />);
    await screen.findByRole("heading", { level: 1 });
    expect(screen.queryByRole("button", { name: "Məlumatları göstər" })).not.toBeInTheDocument();
    expect(screen.queryByText("Tibbi profil")).not.toBeInTheDocument();
    expect(calls.some((c) => path(c).endsWith("/medical-profile"))).toBe(false);
  });
});

describe("user administration pages", async () => {
  const { default: UsersPage } = await import("@/app/(app)/users/page");
  const { default: RolesPage } = await import("@/app/(app)/roles/page");

  const roles = {
    roles: [
      { code: "director", name: "Direktor", isSystem: true, editable: false, permissions: [{ permission: "patient:read", scope: "tenant", maxAmount: null }] },
      { code: "cashier", name: "Kassir", isSystem: true, editable: true, permissions: [{ permission: "invoice:refund", scope: "branch", maxAmount: 0 }] },
      { code: "hygienist", name: "Gigiyenist", isSystem: true, editable: true, permissions: [] },
    ],
    permissions: [
      { code: "patient:read", description: "Pasiyentə baxış" },
      { code: "invoice:refund", description: "Geri qaytarma" },
      { code: "user:manage", description: "İstifadəçi idarəsi" },
    ],
  };
  const aysel = { id: "u1", fullName: "Aysel Əliyeva", email: "a***@clinic.az", status: "active", lastLoginAt: null, twoFactorEnabled: false, roles: [{ role: "cashier", branchId: null }] };
  const locked = { id: "u2", fullName: "Murad Quliyev", email: "m***@clinic.az", status: "locked", lastLoginAt: "2026-10-26T10:00:00Z", twoFactorEnabled: false, roles: [{ role: "hygienist", branchId: null }] };
  const sign = (r: Request) => `${r.method} ${path(r)}`;

  it("lists users with role names and status, and offers enable for a locked account and none to disable yourself", async () => {
    perms = ["user:manage"];
    handler = (r) => (path(r) === "/roles" ? json(200, roles) : json(200, { items: [{ ...aysel, id: "u" }, locked], nextCursor: null }));
    renderWithQuery(<UsersPage />);
    expect(await screen.findByText("Murad Quliyev")).toBeInTheDocument();
    expect(screen.getAllByText("Kassir").length).toBeGreaterThan(0);
    expect(screen.getByText("Bloklanıb")).toBeInTheDocument();
    // mockAuth istifadəçisinin id-si "u"-dur: öz sətri üçün söndürmə və rol dəyişmə bağlıdır
    expect(screen.getByRole("button", { name: "Söndür: Aysel Əliyeva" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Rolları dəyiş: Aysel Əliyeva" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Aç: Murad Quliyev" })).toBeEnabled();
  });

  it("enables a locked account", async () => {
    perms = ["user:manage"];
    handler = (r) => {
      if (path(r) === "/roles") return json(200, roles);
      if (r.method === "POST") return json(200, { ...locked, status: "active" });
      return json(200, { items: [locked], nextCursor: null });
    };
    renderWithQuery(<UsersPage />);
    await userEvent.click(await screen.findByRole("button", { name: "Aç: Murad Quliyev" }));
    await waitFor(() => expect(calls.map(sign)).toContain("POST /users/u2/enable"));
  });

  it("creates a user, checks the password locally and maps a duplicate-email error", async () => {
    perms = ["user:manage"];
    let status = 201;
    handler = (r) => {
      if (path(r) === "/roles") return json(200, roles);
      if (r.method === "POST") return status === 201 ? json(201, aysel) : json(409, { code: "user.email_exists" });
      return json(200, { items: [], nextCursor: null });
    };
    renderWithQuery(<UsersPage />);
    await userEvent.click(await screen.findByRole("button", { name: "Yeni istifadəçi" }));
    await userEvent.type(screen.getByLabelText("E-poçt"), "yeni@clinic.az");
    await userEvent.type(screen.getByLabelText("Ad Soyad"), "Yeni İşçi");
    await userEvent.selectOptions(screen.getByLabelText("Rol"), "cashier");
    await userEvent.type(screen.getByLabelText("İlkin parol"), "short1");
    expect(screen.getByRole("button", { name: "Yarat" })).toBeDisabled();
    expect(screen.getByText("Parol ən azı 10 simvol olmalıdır.")).toBeInTheDocument();

    await userEvent.clear(screen.getByLabelText("İlkin parol"));
    await userEvent.type(screen.getByLabelText("İlkin parol"), "Str0ng-Passw0rd");
    status = 409;
    await userEvent.click(screen.getByRole("button", { name: "Yarat" }));
    expect(await screen.findByText("Bu e-poçt ilə istifadəçi artıq var.")).toBeInTheDocument();

    status = 201;
    await userEvent.click(screen.getByRole("button", { name: "Yarat" }));
    expect(await screen.findByText("İstifadəçi yaradıldı.")).toBeInTheDocument();
    const post = calls.filter((c) => sign(c) === "POST /users").at(-1)!;
    expect(await post.json()).toEqual({ email: "yeni@clinic.az", fullName: "Yeni İşçi", password: "Str0ng-Passw0rd", roles: [{ role: "cashier", branchId: null }] });
  });

  it("replaces a user's roles keeping existing branches", async () => {
    perms = ["user:manage"];
    handler = (r) => {
      if (path(r) === "/roles") return json(200, roles);
      if (r.method === "PUT") return json(200, locked);
      return json(200, { items: [locked], nextCursor: null });
    };
    renderWithQuery(<UsersPage />);
    await userEvent.click(await screen.findByRole("button", { name: "Rolları dəyiş: Murad Quliyev" }));
    await userEvent.click(await screen.findByLabelText("Kassir"));
    await userEvent.click(screen.getByRole("button", { name: "Saxla" }));
    await waitFor(() => expect(calls.map(sign)).toContain("PUT /users/u2/roles"));
    const put = calls.find((c) => sign(c) === "PUT /users/u2/roles")!;
    expect(await put.json()).toEqual({ roles: [{ role: "hygienist", branchId: null }, { role: "cashier", branchId: null }] });
  });

  it("resets a password only when it meets the policy", async () => {
    perms = ["user:manage"];
    handler = (r) => (path(r) === "/roles" ? json(200, roles) : r.method === "POST" ? new Response(null, { status: 204 }) : json(200, { items: [locked], nextCursor: null }));
    renderWithQuery(<UsersPage />);
    await userEvent.click(await screen.findByRole("button", { name: "Parolu sıfırla: Murad Quliyev" }));
    await userEvent.type(screen.getByLabelText("Yeni parol"), "weak");
    expect(screen.getByRole("button", { name: "Sıfırla" })).toBeDisabled();
    await userEvent.clear(screen.getByLabelText("Yeni parol"));
    await userEvent.type(screen.getByLabelText("Yeni parol"), "Another-Passw0rd");
    await userEvent.click(screen.getByRole("button", { name: "Sıfırla" }));
    expect(await screen.findByText("Parol dəyişdi, sessiyalar bağlandı.")).toBeInTheDocument();
    expect(await calls.find((c) => sign(c) === "POST /users/u2/reset-password")!.json()).toEqual({ newPassword: "Another-Passw0rd" });
  });

  it("shows administrator roles read-only and saves an edited role without user:manage", async () => {
    perms = ["user:manage"];
    handler = (r) => (r.method === "PUT" ? json(200, roles.roles[1]) : json(200, roles));
    renderWithQuery(<RolesPage />);
    expect(await screen.findByText("Bu rol dəyişdirilə bilməz")).toBeInTheDocument();
    expect(screen.getByLabelText("patient:read icazəsi")).toBeChecked();
    expect(screen.getByLabelText("patient:read icazəsi")).toBeDisabled();
    expect(screen.queryByLabelText("user:manage icazəsi")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Saxla" })).not.toBeInTheDocument();

    await userEvent.selectOptions(screen.getByLabelText("Rol"), "cashier");
    await userEvent.click(screen.getByLabelText("patient:read icazəsi"));
    await userEvent.selectOptions(screen.getByLabelText("patient:read əhatəsi"), "own");
    await userEvent.type(screen.getByLabelText("Geri qaytarma limiti"), "5");
    await userEvent.click(screen.getByRole("button", { name: "Saxla" }));
    expect(await screen.findByText(/İcazələr saxlandı/)).toBeInTheDocument();
    const put = calls.find((c) => sign(c) === "PUT /roles/cashier/permissions")!;
    expect(await put.json()).toEqual({
      permissions: [
        { permission: "invoice:refund", scope: "branch", maxAmount: 5 },
        { permission: "patient:read", scope: "own", maxAmount: null },
      ],
    });
  });

  it("maps a protected-role rejection", async () => {
    perms = ["user:manage"];
    handler = (r) => (r.method === "PUT" ? json(409, { code: "role.protected" }) : json(200, roles));
    renderWithQuery(<RolesPage />);
    await userEvent.selectOptions(await screen.findByLabelText("Rol"), "hygienist");
    await userEvent.click(screen.getByLabelText("patient:read icazəsi"));
    await userEvent.click(screen.getByRole("button", { name: "Saxla" }));
    expect(await screen.findByText("İdarəçi rolları dəyişdirilə bilməz.")).toBeInTheDocument();
  });
});
