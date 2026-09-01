import { createHmac } from "node:crypto";

export function hmacText(secret: string, value: string): string {
  return createHmac("sha256", secret).update(value).digest("hex");
}
