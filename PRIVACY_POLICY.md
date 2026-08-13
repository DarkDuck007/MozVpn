# Privacy Policy

**Last Updated: August 13, 2026**

This Privacy Policy explains how MozVpn ("the App") handles your information. MozVpn is a personal, open-source VPN utility designed to tunnel device network traffic locally using a SOCKS/HTTP proxy core.

We are committed to protecting your privacy. This policy describes our practices regarding data collection, storage, and processing.

---

## 1. Strictly No Logs Policy (Data We Do Not Collect)
We do not collect, log, store, or share any of your network traffic, browsing history, or personal data. 

Specifically, we do NOT log:
*   Your IP address (source or destination).
*   Websites you visit or services you connect to.
*   DNS queries.
*   Data packets transmitted through the VPN tunnel.
*   Personal identifier information (names, emails, device IDs).

---

## 2. On-Device Local Processing
All VPN encapsulation, proxying, and routing operations are performed **entirely on your local device**. 
*   Network traffic captured by the VPN interface is routed through a local SOCKS/HTTP proxy port listening on `127.0.0.1`.
*   The App does not operate, host, or connect to any centralized data collection servers.

---

## 3. App Permissions and Usage
The App requests the following permissions to perform its core network routing duties:

*   **`BIND_VPN_SERVICE` (VPN Service):** 
    Required to establish a local Virtual Private Network (TUN) interface on your device. This allows the App to route your device's traffic through the local proxy server. No traffic is monitored or logged during this process.
*   **`POST_NOTIFICATIONS` (Post Notifications):**
    Used on Android 13 (API 33) and newer to show an ongoing notification when the VPN tunnel is active. This notification informs you that the VPN is running and provides a quick shortcut to open the App.

---

## 4. Third-Party Services
The App is open-source and does not integrate third-party analytics platforms, crash reporting suites, trackers, or advertising SDKs. No data is shared with third parties.

---

## 5. Security
Because the App does not collect or transmit your data to any remote database, there is no risk of data breach from our side. All traffic encryption and security depend entirely on the proxy endpoint configurations you supply to the App.

---

## 6. Changes to This Privacy Policy
We may update our Privacy Policy from time to time. We will notify you of any changes by posting the new Privacy Policy on this page and updating the "Last Updated" date at the top.

---

## 7. Contact Us
If you have any questions or suggestions about this Privacy Policy, please open an issue on the GitHub Repository: [DarkDuck007/MozVpn](https://github.com/DarkDuck007/MozVpn).
