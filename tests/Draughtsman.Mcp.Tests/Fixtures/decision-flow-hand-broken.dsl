draughtsman 1
id doc_ticket_triage_decision_flow
type flowchart
title "Support Desk: ticket flow"

# The pool is vertical: the three actor lanes are columns and time runs down the page.
node pool_main pool "Support Desk: nightly digest and per-minute ticket flow" @ 0,0 1580x2964 orientation="vertical"
node lane_agent lane "Agent: quick first-line triage (queue tier 1)" @ 0,48 440x2916 parent=pool_main style.fill=#EEF5FF style.stroke=#2F6FDB
node lane_code lane "Automation: inbox, rules, limits, routing, ledgers" @ 440,48 760x2916 parent=pool_main style.fill=#F7F7F5 style.stroke=#475569
node lane_expert lane "Expert: senior reviewer, once a day" @ 1200,48 380x2916 parent=pool_main style.fill=#FFF8E8 style.stroke=#B7791F
node g_nightly text "NIGHTLY CYCLE\nIntake, digest, case" @ -270,222 240x64 style.bold=true style.fontSize=15
node g_new text "EVERY MINUTE, NEW\nIntake funnel, per queue" @ -270,1370 240x64 style.bold=true style.fontSize=15
node g_active text "EVERY MINUTE, ACTIVE\nClosure checks, per queue" @ -270,2160 240x64 style.bold=true style.fontSize=15
node note_agent note "The agent only answers narrow questions:\nurgency (Yes/No), tone (Choice),\nseverity (Score) and duplicate\ndetection (Yes/No). It never writes a\nreply and never closes a ticket." @ 40,116 360x130 parent=lane_agent style.fill=#FFFBE6 style.stroke=#C9B458
node note_expert note "Called once per queue per day,\nnever from the minute loop. Tokens,\ncost and latency of every call\nare recorded." @ 1240,1010 300x100 parent=lane_expert style.fill=#FFFBE6 style.stroke=#C9B458
node note_poll note "Each minute, per queue: new\nruns the intake funnel, active\nruns the closure checks." @ 950,1355 230x86 parent=lane_code style.fill=#FFFBE6 style.stroke=#C9B458
node n_begin terminal "Inboxes polled every 5 minutes,\nreports hourly" @ 560,116 280x56 parent=lane_code style.fill=#DDF1E4 style.stroke=#2E7D4F
node n_intake io "Collect tickets: email from four\nsupport inboxes, chat transcripts,\nweb form if enabled" @ 525,222 350x100 parent=lane_code style.fill=#EDEFF3 style.stroke=#475569
node n_normalise process "Dedupe by message hash; store\ntickets and customer details" @ 570,372 260x84 parent=lane_code ports=[l@0,0.5] style.fill=#EDEFF3 style.stroke=#475569
node n_classify process "Triage each new ticket per queue:\nurgency, tone,\nseverity" @ 80,506 280x108 parent=lane_agent ports=[t@0.5,0 r@1,0.5] style.fill=#D6E6FF style.stroke=#2F6FDB
node n_labels stored-data "Scored tickets per queue:\nurgency, tone,\nseverity" @ 560,515 280x90 parent=lane_code ports=[l@0,0.5] style.fill=#DDE3EC style.stroke=#475569
node n_nightly terminal "Daily 07:00 UTC per queue,\nor at startup when no\nvalid case exists" @ 940,686 250x76 parent=lane_code style.fill=#DDF1E4 style.stroke=#2E7D4F
node n_digest process "Build the daily digest per queue:\nvolume stats, backlog, SLA &\nbreaches, top scored tickets" @ 560,670 280x108 parent=lane_code ports=[r@1,0.5 b@0.5,1] style.fill=#EDEFF3 style.stroke=#475569
node n_analysis process "Write the case: summary, tone,\none routing rule, escalations and\nclosure conditions" @ 1245,835 290x130 parent=lane_expert ports=[in@0,0.3 out@0,0.75] style.fill=#FFE6B3 style.stroke=#B7791F
node n_review decision "Case well-formed\nand within bounds?" @ 540,1010 320x130 parent=lane_code ports=[in@0.75,0] style.fill=#EDEFF3 style.stroke=#475569
node n_bounce process "Rejected or out of bounds:\nstore an empty case\n(no routing)" @ 950,1033 230x84 parent=lane_code style.fill=#EDEFF3 style.stroke=#475569
node n_case stored-data "Case on file: valid until, tone,\nrouting rule, escalations, closure\nconditions" @ 560,1210 280x108 parent=lane_code ports=[r@1,0.5] style.fill=#DDE3EC style.stroke=#475569
node n_poll terminal "Every minute, per queue" @ 580,1370 240x56 parent=lane_code style.fill=#DDF1E4 style.stroke=#2E7D4F
node n_e_open decision "Valid, unexpired case\nwith priority high?" @ 540,1480 320x130 parent=lane_code style.fill=#EDEFF3 style.stroke=#475569
node n_idle1 terminal "No routing; check again\nnext minute" @ 950,1517 230x56 parent=lane_code style.fill=#F8FAFC style.stroke=#94A3B8
node n_e_sla decision "Case's routing rule met\non the minute checks?" @ 540,1660 320x130 parent=lane_code ports=[l@0,0.5] style.fill=#EDEFF3 style.stroke=#475569
node n_idle2 terminal "No routing; check again\nnext minute" @ 950,1697 230x56 parent=lane_code style.fill=#F8FAFC style.stroke=#94A3B8
node n_e_dup decision "Agent gate: does a new reply\nduplicate the case?" @ 60,1830 320x130 parent=lane_agent ports=[t@0.5,0 r@1,0.5] style.fill=#D6E6FF style.stroke=#2F6FDB
node n_skip_dup terminal "Case duplicated by reply:\nno routing" @ 100,2010 240x56 parent=lane_agent style.fill=#F8FAFC style.stroke=#94A3B8
node n_e_perm decision "Limits pass: daily\ncap, cooldown, kill switch?" @ 540,1830 320x130 parent=lane_code style.fill=#EDEFF3 style.stroke=#475569
node n_idle3 terminal "No routing; check again\nnext minute" @ 950,1867 230x56 parent=lane_code style.fill=#F8FAFC style.stroke=#94A3B8
node n_assign process "Assign through the router,\nSLA ledgers and continuity" @ 570,2010 260x100 parent=lane_code style.fill=#EDEFF3 style.stroke=#475569
node n_assigned stored-data "Ticket assigned: owner, case id,\nreply ids behind the decision" @ 560,2160 280x90 parent=lane_code style.fill=#DDE3EC style.stroke=#475569
node n_x_done decision "Target, deadline, max wait,\ncase expiry or a new\nempty case hit?" @ 540,2300 320x140 parent=lane_code ports=[l@0,0.5] style.fill=#EDEFF3 style.stroke=#475569
node n_x_reply decision "Agent: does a new material\nreply change\nthe case?" @ 60,2490 320x130 parent=lane_agent ports=[t@0.5,0 r@1,0.5] style.fill=#D6E6FF style.stroke=#2F6FDB
node n_close process "Close through the router,\nSLA ledgers and continuity" @ 570,2505 260x100 parent=lane_code style.fill=#EDEFF3 style.stroke=#475569
node n_wait terminal "Wait; check again\nnext minute" @ 100,2670 240x56 parent=lane_agent style.fill=#F8FAFC style.stroke=#94A3B8
node n_log process "Record the ticket life:\nopen time, touches, net result" @ 570,2670 260x84 parent=lane_code style.fill=#EDEFF3 style.stroke=#475569
node n_survey terminal "Measure the net result against\nthe SLA target over weeks" @ 550,2820 300x84 parent=lane_code style.fill=#DDF1E4 style.stroke=#2E7D4F

edge e_01 flow n_begin -> n_intake routing=orthogonal
edge e_02 flow n_intake -> n_normalise routing=orthogonal
edge e_03 flow n_normalise.l -> n_classify.t routing=orthogonal
edge e_04 flow n_classify.r -> n_labels.l routing=orthogonal
edge e_05 flow n_labels -> n_digest routing=orthogonal
edge e_06 flow n_nightly -> n_digest.r routing=orthogonal
edge e_07 flow n_digest.b -> n_analysis.in routing=orthogonal
edge e_08 flow n_analysis.out -> n_review.in routing=orthogonal
edge e_09 flow n_review -> n_case "Yes" routing=orthogonal
edge e_10 flow n_review -> n_bounce "No" routing=orthogonal
edge e_11 flow n_bounce -> n_case.r routing=orthogonal
edge e_12 flow n_case -> n_poll routing=orthogonal
edge e_13 flow n_poll -> n_e_open routing=orthogonal
edge e_14 flow n_e_open -> n_e_sla "Yes" routing=orthogonal
edge e_15 flow n_e_open -> n_idle1 "No" routing=orthogonal
edge e_16 flow n_e_sla.l -> n_e_dup.t "Yes" routing=orthogonal
edge e_17 flow n_e_sla -> n_idle2 "No" routing=orthogonal
edge e_18 flow n_e_dup.r -> n_e_perm "No: case stands" routing=orthogonal
edge e_19 flow n_e_dup -> n_skip_dup "Yes" routing=orthogonal
edge e_20 flow n_e_perm -> n_assign "Yes" routing=orthogonal
edge e_21 flow n_e_perm -> n_idle3 "No" routing=orthogonal
edge e_22 flow n_assign -> n_assigned routing=orthogonal
edge e_23 flow n_assigned -> n_x_done routing=orthogonal
edge e_24 flow n_x_done -> n_close "Yes" routing=orthogonal
edge e_25 flow n_x_done.l -> n_x_reply.t "No" routing=orthogonal
edge e_26 flow n_x_reply.r -> n_close "Yes" routing=orthogonal
edge e_27 flow n_x_reply -> n_wait "No" routing=orthogonal
edge e_28 flow n_close -> n_log routing=orthogonal
edge e_29 flow n_log -> n_survey routing=orthogonal

flythrough loop=false
keyframe k_overview @ -290,0 1880x2964 caption="Three actors: quick agent, the daily expert reviewer and automation"
keyframe k_research @ 0,80 1200x720 caption="Intake: tickets are collected and deduplicated by code, then triaged per queue by the agent"
keyframe k_case @ 440,640 1140x700 caption="Once a day the expert writes a case from a compact digest; code validates it"
keyframe k_entry @ 0,1340 1200x760 caption="Every minute, new: the case and the checks decide, the agent gates on fresh replies, limits have the last word"
keyframe k_exit @ 0,2130 1200x700 caption="Active: deadline rules and an agent reply check decide the close"
keyframe k_measure @ 440,2540 760x424 caption="Every ticket is measured against the SLA target"
