# Encryption

LiteDB uses salted AES (as defined by [RFC 2898](https://tools.ietf.org/html/rfc2898)) as its encryption. This is implemented by the `Rfc2898DeriveBytes` class.

The `Aes` object used for cryptography is initialized with `PaddingMode.None` and `CipherMode.ECB`.

The password for an encrypted datafile is defined in the connection string (for more info, check [Connection String](../connection-string)). The password can only be changed or removed by rebuilding the datafile (for more info, check Rebuild Options in [Pragmas](../pragmas#rebuildOptions)).


---

*Made with ♥ by the LiteDB team – [@mbdavid](https://twitter.com/mbdavid) – MIT License.*
