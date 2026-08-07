export interface AuthProvider {
  key: string;
  displayName: string;
}

export interface AuthAccount {
  id: string;
  displayName: string;
  email: string | null;
  avatarUrl: string | null;
  externalProviders: string[];
}

export interface AuthMe {
  isGuest: boolean;
  account: AuthAccount | null;
}

export class AuthApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
    this.name = "AuthApiError";
  }
}

const BASE = "/api/v1/auth";

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`${BASE}${path}`, {
      ...init,
      credentials: "include",
      headers: {
        Accept: "application/json",
        ...(init?.body ? { "Content-Type": "application/json" } : {}),
        ...init?.headers,
      },
    });
  } catch {
    throw new AuthApiError("Failed to reach the authentication API", 0);
  }

  if (response.status === 204) return undefined as T;
  if (!response.ok) {
    throw new AuthApiError(`Authentication request failed: ${response.status}`, response.status);
  }

  return (await response.json()) as T;
}

export function listProviders(): Promise<{ providers: AuthProvider[] }> {
  return request("/providers");
}

export function getMe(): Promise<AuthMe> {
  return request("/me");
}

export function logout(): Promise<void> {
  return request("/logout", { method: "POST" });
}
