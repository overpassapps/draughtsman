draughtsman 1
id doc_checkout
type sequence
title "Checkout"
meta created=2026-09-29T00:00:00.000Z updated=2026-09-29T00:00:00.000Z by=eric

node p_user actor "Customer"
node p_web participant "Web app"
node p_api participant "Orders API"
node p_pay participant "Payments"

edge m_1 sync p_user -> p_web "Place order"
edge m_2 sync p_web -> p_api "POST /orders" activate=true
edge m_3 sync p_api -> p_pay "charge(total)" activate=true
edge m_4 return p_pay -> p_api "receipt" deactivate=true
edge m_5 async p_api -> p_web "declined"
edge m_6 return p_api -> p_web "201 Created" deactivate=true
edge m_7 sync p_api -> p_api "audit()"
