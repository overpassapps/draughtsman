draughtsman 1
id doc_checkout_fragments
type sequence
title "Checkout with a fragment, a note, and a created participant"
meta created=2026-09-29T00:00:00.000Z updated=2026-09-29T00:00:00.000Z by=eric

node p_user actor "Customer"
node p_web participant "Web app"
node p_api participant "Orders API"
node p_pay participant "Payments"
node p_mail participant "Mailer"
node f_1 fragment "alt" operator="alt" operands=["[card ok]","[declined]"]

edge m_1 sync p_user -> p_web "Place order"
edge m_2 sync p_web -> p_api "POST /orders" activate=true
edge n_1 note p_web -> p_api "Idempotent by key"
edge m_3 sync p_api -> p_pay "charge(total)" activate=true in="f_1" operand=0
edge m_4 return p_pay -> p_api "receipt" deactivate=true in="f_1" operand=0
edge m_5 async p_api -> p_web "declined" in="f_1" operand=1
edge m_6 return p_api -> p_web "201 Created" deactivate=true
edge m_7 create p_web -> p_mail "new Mailer()"
edge m_8 sync p_web -> p_mail "send(receipt)"
edge m_9 destroy p_web -> p_mail "close()"
