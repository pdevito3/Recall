import { z } from 'zod'

// The Content-Security-Policy of the Server refuses eval, and Zod reports a violation when it tests for eval.
z.config({ jitless: true })
