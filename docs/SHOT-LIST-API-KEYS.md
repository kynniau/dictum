# Shot list: screenshots for the API key guides (site/help.html, section 9)

For Kyle. Written 04/11/2026 by the K-Docs lane. Nothing here is a screenshot: the lane cannot sign in to the providers' sites
and has not captured any Dictum screen. The help page carries `<!-- SCREENSHOT n: see docs/SHOT-LIST-API-KEYS.md -->` comments where
each picture goes. No image is on the page until you supply it.

Rules (design brief `.demo-preview/design-api-keys.md` §4 and decision D5):

- Take them from your own accounts. Crop to the control being described, not the whole browser.
- Blank everything that is yours before saving: your name, email address, organisation, project and workspace names, the
  Azure subscription and resource names, and every key. A key must never appear, not even partly. Delete any key you create for
  a picture afterwards, in the provider's console.
- Names only, no provider logos in the picture's crop where you can avoid them.
- Each guide gets one caption in the form "Screens as of DD/MM/YYYY", with the day you took them. Fill in the date; do not leave
  the letters in place on the page.
- Every image needs alt text that says what the picture shows, in the page's plain style. Save as PNG in `site/img/keys/`.
- A provider's screens change. Check each one against the step beside it on the day you take it; if a label differs from the
  step, tell the lane so the step is corrected before the picture goes in.

## Provider screens (from your own accounts)

| # | Part of the guide | What to capture | Which step it sits beside | Blank before saving |
|---|---|---|---|---|
| 2 | Anthropic (Claude) | The Claude Console API keys page (`platform.claude.com/settings/keys`) with the **Create key** button, then the create-key form showing **Linked account** and the expiration choice | Step 3, "Click Create key" | Account name, email, organisation, workspace, any listed key names |
| 3 | OpenAI | The dashboard screen where the **Create an API Key** button is | Step 2, "click Create an API Key" | Account, organisation and project names, any listed key |
| 4 | Google Gemini | Google AI Studio's **API Keys** page with the **Create API key** button | Step 2, "click Create API key" | Account, project names, any listed key |
| 5 | Azure OpenAI | The Azure portal resource page: **Overview** showing the **Endpoint**, and **Resource Management** > **Keys and Endpoint** | Step 4. Take it from an Azure OpenAI resource, since Microsoft's page names these menus for a Foundry resource | Subscription, resource group and resource names, the endpoint's resource name, both keys |
| 6 | Groq | The Groq console API Keys page (`console.groq.com/keys`) with **Create API Key** | Step 2, "Click Create API Key" | Account, team names, any listed key |

## Dictum screens (the lane can describe them; Kyle or the lane captures them from a build that has the key list)

| # | Where | Show | Notes |
|---|---|---|---|
| 1 | Settings > Clean-up, Cloud clean-up card | The **Service** list, the key list under it (an empty list says "No key yet."), and the **Add a key…** button | Use a fresh profile so no real key name is listed |
| 7 | The **Add a key** window | The title, the **Label** and **Key** fields (the Key field shows dots), and **Save**, **Cancel** and the **How do I get a key?** link | Paste a fake value such as `not-a-real-key`; it shows as dots. Never type a real key |
| 8 | Settings > Clean-up, key list with one stored key | A key marked **Active**, with **Check this key** and **Remove** beside it | Label it with a made-up name such as "Work" |
| 9 | Settings > Clean-up, after **Check this key** | The result line ("The key works." or the refusal line) | Only with a throwaway key, and only against the service you agreed to in the consent step |
| 10 | Settings > Models, Advanced view, Cloud speech card | The **Service** list with Azure OpenAI or Groq chosen, **Server address**, **Model**, the key list and **Add a key…** | For the Azure and Groq parts |
| 11 | Settings > Privacy, **API keys** card | The card's line and **Remove all keys…** | Optional; the guides do not need it |

Slots 7 to 11 have no comment on the page yet. Say which you want and where they go, and the lane will add the comment and the
caption, or add them yourself in the same form.
