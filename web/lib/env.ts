import "server-only";
import { z } from "zod";

const envSchema = z.object({
  API_BASE_URL: z.url(),
  NEXT_PUBLIC_SITE_URL: z.url(),
});

export const env = envSchema.parse({
  API_BASE_URL: process.env.API_BASE_URL,
  NEXT_PUBLIC_SITE_URL: process.env.NEXT_PUBLIC_SITE_URL,
});
