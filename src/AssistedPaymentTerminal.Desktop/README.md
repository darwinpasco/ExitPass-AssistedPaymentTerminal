# Desktop Shell

This is a thin Windows WPF host for the React terminal UI using Microsoft Edge WebView2.

Responsibilities in this slice:

- validate `APT_PROFILE`;
- load the Vite development URL during local development;
- load packaged frontend assets when built;
- show startup/WebView failures clearly.

It does not contain tariff, payment-finality, fiscal-numbering, receipt, cash drawer, card terminal, printer, scanner, customer display, or gate-control logic.

Future kiosk hardening should be added as a dedicated production entry point after device identity, Windows lockdown policy, update policy, and support operations are approved.
