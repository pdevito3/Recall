# SecLists

The Server carries a list of common and leaked passwords, `Auth/Passwords/common-passwords.txt`, inside `Recall.Server.dll`. It refuses a chosen password that is on the list.

The list comes from the file `Passwords/Common-Credentials/xato-net-10-million-passwords-1000000.txt` of SecLists. It keeps each entry of 15 or more code points, in NFKC and in lower case, with no repeat. `scripts/build-password-lists.cs` builds it.

SecLists is licensed under the MIT License. The text of the license is in `LICENSE`.

Source: https://github.com/danielmiessler/SecLists/blob/64e7d0b910015fa4e8dffa9fed7aba7d73e4673d/Passwords/Common-Credentials/xato-net-10-million-passwords-1000000.txt

SHA-256 of the source: `424a3e03a17df0a2bc2b3ca749d81b04e79d59cb7aeec8876a5a3f308d0caf51`
