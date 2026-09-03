/** Whether a thrown error is MongoDB's duplicate-key violation (unique index conflict). */
export function isDuplicateKeyError(error: unknown): boolean {
  return typeof error === "object" && error !== null && "code" in error && (error as { code: unknown }).code === 11000;
}
