/**
 * Injectable clock seam. Services derive current-state freshness and expiry
 * from the clock instead of reading the system time directly, so integration
 * tests can drive age and offline transitions deterministically.
 */
export interface Clock {
  now(): Date;
}

export const systemClock: Clock = {
  now: () => new Date(),
};
