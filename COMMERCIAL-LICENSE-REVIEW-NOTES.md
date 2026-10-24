# Commercial licence review notes

**Reviewed by: not yet reviewed by a solicitor.**

[`COMMERCIAL-LICENSE.md`](COMMERCIAL-LICENSE.md) is version 1.0 of Draughtsman's commercial licence, the paid alternative to the
AGPL-3.0. It was made on 24 October 2026 from the private repository's EULA template, after every bracket had been settled with the
director's approved defaults ("keep defaults, fill in what you can"). Because Draughtsman is AGPL software with no licence
system, every clause about a licence file, a signing key, an evaluation period or an unlicensed copy was removed.

Most of the text is the template's. **Four passages are new**, because the template was written for closed-source software. Ask a
solicitor to read these first:

- the opening paragraph, "What the commercial licence gives you that the AGPL does not";
- 2.2, the right to modify;
- 2.3, the relationship to the AGPL;
- 13.4 and 10.3, falling back to the AGPL when the agreement ends.

The marketing plan's trigger for a solicitor (item P3) is "before the first sale over £3,000 or the first enterprise buyer". An
Unlimited sale is exactly £3,000, so the first one is the natural point to get a fixed-fee review.

## Clauses a solicitor should look at

1. **2.3, the licence as an alternative to the AGPL.** This is the whole product. Check that it works as a grant of different terms
   by the copyright holder. Check that it does not conflict with the AGPL rights other people already hold. Check that 14.2 limits
   it to the parts Overpass owns. That last point depends on [`CLA.md`](CLA.md) getting Overpass the right to relicense
   outside contributions.
2. **11.3, the liability cap can fall to £0.** The cap is "the Fee you paid us in the 12 months before the event". The licence is
   perpetual and renewal is optional. A customer who bought in year one and never renewed would face a cap of £0 for a claim in
   year two. Standard business terms must pass the reasonableness test in the Unfair Contract Terms Act 1977, and a cap that can be
   zero is the clause most likely to fail it. If it fails, the cap does not apply at all. Ask whether the cap needs a floor, for
   example the higher of that figure and the original Fee.
3. **11.2, the exclusion of loss of data.** It rests on the customer keeping backups (10.5). A court tests it on the same basis as
   11.3.
4. **6.1(a) and 2.2, internal use only.** Customers may modify, but may not pass the Software on under these terms. Check that
   "internal business" and "your organisation" are tight enough for groups of companies and for contractors who keep copies.
5. **7.4, the no-phone-home promise.** It binds every later Build for as long as any customer holds a licence, with no end date
   and no exception for a security need.
6. **4.4, critical security fixes after the update period.** The fixed Build is public under the AGPL anyway. The clause only lets
   a lapsed customer use it under these terms. Check that this is as narrow as intended.
7. **11.1, "between businesses".** Nothing stops a sole trader buying. Ask whether the order process needs a "buying for a business"
   confirmation so the Consumer Rights Act 2015 stays out of scope.

## Facts filled in, and where they came from

| Clause | Value | Source |
| --- | --- | --- |
| 1.1 | OVERPASS LIMITED, company number 05074817, England and Wales | Companies House record for 05074817, read 24 October 2026 |
| 1.1 | Registered office 124 City Road, London, England, EC1V 2NX | Companies House, same record. The brief asked for "124 City Road, Unit 4, London EC1V 2NX". That is the address on the Google Play developer profile, not the registered office. A contract that names a registered office should match Companies House, so the Companies House wording is used. Change it only if Companies House changes. |
| 1.1 | VAT number GB 833 2334 52 | The `vatNumber` the Overpass website publishes (`overpass-website/src/data/siteConfig.ts`). It has not been checked against HMRC. |
| 7.5 | <https://overpass.co.uk/privacy-policy/> | The live Overpass privacy policy (read 24 October 2026). **It covers the website only.** It does not mention licence customers, payment records or order records. Add a short paragraph on those before the first sale. |
| 8, 9, 16.4 | draughtsman@overpass.co.uk | The address `README.md` and `SECURITY.md` use. Whether this mailbox reaches an inbox the director reads (plan item P4) is still unchecked. |
| 8.1, 8.2 | 90 days; 3 and 10 working days; 30 days | Identical to `SECURITY.md`. If one changes, change the other. |

## Decisions applied

| Clause | Decision |
| --- | --- |
| 3.1, 2.4 | Team £1,500 for up to 50 people; Unlimited £3,000; one-off, excluding VAT |
| 5.2 | Renewal of updates at 25% of list a year: £375 Team, £750 Unlimited |
| 11.3 | Liability capped at the Fee paid in the 12 months before the claim (see item 2 above) |
| 11.4 | Carve-outs from the cap: the Fee, use outside clause 2, and breach of clause 6 or 12 |
| Not included | Source escrow (the source is public); the IP indemnity; any evaluation period (anyone may run it under the AGPL) |
| 3.3 | Payment at checkout, or invoice with 30-day terms. No checkout exists yet. Until one does, every sale goes by invoice (plan item P7), so the invoice must link to this document. |
| 4.3 | 30 days' grace for a Build made after the Entitlement End Date |
| 9 | Email support until the Entitlement End Date, Monday to Friday 09:00 to 17:30 UK time; targets of 2 working days, or 1 for an outage. No priority support is offered, and the README promises none. |
| 10.1 to 10.3 | 90-day warranty on unmodified Covered Builds; repair or replace, then refund; a 30-day no-questions refund |
| 12.5, 13.2, 15.3 | 5 years' confidentiality; 30 days to cure; 30 days to settle before court |
| 15 | England and Wales law; the courts of England and Wales have exclusive jurisdiction |
| 16.3 | A customer may transfer the licence on a sale of its whole business, with notice |
| 16.4 | Notices to Overpass go by email only |
| 16.9 | Publicity is opt-in only |
| 16.10 | Accepted by signing the Order Details, or by click-through at checkout |

## Removed from the template, and why

- **The Licence File, the key and the offline check (template clause 4).** The public build has no licence system.
- **The evaluation, the evaluation line and the evaluation period (template clause 7).** Anyone may run the Software under the AGPL
  without limit.
- **The bans on modifying, decompiling and bypassing licence checks (template 8.1(c), (e) and (f)).** The source is public, and
  modifying it is what customers pay for.
- **"The Software as a whole is proprietary and is not open source" (template 18.3).** That is no longer true.
- **Source escrow (template clause 12).** The source is already public.
