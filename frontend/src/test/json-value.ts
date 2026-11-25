/** A value that `JSON.parse` gives, such as the body of a request that a test reads back. */
export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue }
