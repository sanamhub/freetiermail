# FreeTierMail.Smtp

[FreeTierMail](https://github.com/sanamhub/freetiermail) provider for any SMTP relay, through MailKit.
The password goes in `ApiKey`, because most relays use an API key as the SMTP password.

On a loopback host, such as a local [Mailpit](https://mailpit.axllent.org/) inbox, `Username` and
`ApiKey` may be left empty and TLS is used only when the server offers it. Every other host needs a
login and STARTTLS (or TLS on port 465).

**Status: alpha.** The API can change until 1.0.

Source, issues and license (MIT): https://github.com/sanamhub/freetiermail
