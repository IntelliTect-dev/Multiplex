Vary the inputs so the capture exposes the site's parameters rather than one
happy path. Cover whichever of these the goal reaches:

- **guest vs signed in** -- prices, availability and offers frequently differ,
  and the difference is part of the API;
- **every search option** -- round trip vs one-way, a different return location,
  different dates, different passenger or occupancy counts, every filter;
- **every payment option** -- pay now vs pay later, each currency, each
  loyalty or discount code field (leave the field empty if you have no code);
- **every result category** -- each class, tier, rate type or vendor group the
  results are grouped into;
- **detail and breakdown pages** -- the per-result detail view and the price
  breakdown behind it, which is usually where the richest response shape is;
- **extras** -- add-ons, insurance, equipment, seats, upgrades: each one is
  normally its own request;
- **error cases** -- sold out or no availability, a start time too soon, an
  end before the start, an invalid location, an out-of-range count. These
  establish the site's real error taxonomy and are among the highest-value
  entries in a capture.

Stop before any personal-details or payment page: reach it, record the request
that led to it, and go no further.
