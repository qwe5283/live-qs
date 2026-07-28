import { categorizeSpending } from "@ai-life/shared/spending-categories";

export function spendingCategory(merchant: string, product: string, existing?: unknown): string {
  if (typeof existing === "string" && existing && existing !== "uncategorized") {
    return existing;
  }
  return categorizeSpending(merchant, product);
}
