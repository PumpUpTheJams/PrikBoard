---
title: PrikBoard Privacy Policy
---

# PrikBoard Privacy Policy

Last updated: 24 October 2026

This policy explains what the PrikBoard instance at [prikboard.com](https://prikboard.com) (the "Service") and the PrikBoard Discord bot (the "Bot") collect, why, and what you can do about it. It goes with the [Terms of Service](terms-of-service.md).

The Service is run by the PrikBoard maintainers, reachable at prikboardinfo@gmail.com, who are the data controller.

## 1. What we keep about you

PrikBoard is built to keep as little about you as it can.

| What | Why |
| --- | --- |
| A random account id and the **display name** you choose | To show who you are on boards you share |
| A keyed hash (HMAC) of your sign-in provider's account id and of your verified email address | To recognize you when you sign in again, and to find you when someone invites you by email. The hash can't be turned back into the id or the email |
| A copy of your **profile picture** from your sign-in provider | Shown as your avatar. You can remove it at any time, and then it is no longer fetched |
| Your **board memberships** and roles | To decide what you can see and change |
| **Content** you add: boards, cards, images, drawings, tags | That is the Service |
| **Card history**: who changed a card and when | So members can see what changed |
| **Access tokens** you create (stored as hashes) and the agents that use them | To let AI agents and apps act for you |

We do **not** store your name, email address or account id from your sign-in provider in readable form, and we don't store the URL of your profile picture.

## 2. Cookies and browser storage

- A **session cookie** keeps you signed in. It is needed for the Service to work.
- During setup, a short-lived **setup cookie** (one hour) is used by the admin.
- The web app keeps some settings in your browser's local storage, such as window sizes and the mobile or desktop view. They stay on your device.

There are no advertising or analytics cookies, and no third-party trackers.

## 3. Logs

The server logs errors and slow requests to keep the Service running. These logs may include the request path and technical details, and are kept for no longer than 7 days. Rate limiting may use your IP address briefly, in memory.

## 4. The Discord bot

When a Discord server connects a board, the Bot:

- **reads messages** in channels it can see, only to find PrikBoard links. Message content is not stored;
- sends each link and the channel id to the Service to get a preview, then posts that preview in the channel;
- **stores** the server's id and name, the connected channels (ids and names) and an encrypted access token for that server.

The Bot doesn't store Discord user ids or messages, and it keeps no record of the previews it posts. On the Service, a connected server shows as a read-only member of the board, recorded with the PrikBoard user who connected it.

Previews already posted stay in Discord after a board is disconnected; delete them in Discord if needed. Discord's own [privacy policy](https://discord.com/privacy) applies to everything on Discord.

## 5. Who we share it with

We don't sell your data or share it for advertising. Others only see what you share with them:

- **Members** of a board see its content and the display names and avatars of its members.
- **Public boards** can be seen by anyone with the link. The owner chooses whether people's names and avatars show there.
- **Connected Discord servers** see previews of the boards connected to them, in the allowed channels.
- **AI agents** using your access tokens see what those tokens allow.
- Your **sign-in provider** (GitHub, Discord, Google, Steam or another) knows that you signed in to PrikBoard.

The Service is hosted on [prikboard.com](https://prikboard.com).

## 6. How long we keep it

- Your account and content: until you or a board owner deletes them, or the account is deleted.
- Deleted cards, buckets, images and tags: kept for 7 days so they can be restored, then deleted for good.
- Files nothing refers to any more, such as replaced avatars or images of deleted boards: removed within about a day.
- Discord connection data: until the server disconnects or the board owner removes the connection.

## 7. Your rights

Depending on where you live (for example under the EU's GDPR), you can ask to see, correct, export or delete your data, or object to how we use it. You can do much of this yourself: change your display name, remove your avatar, revoke tokens, export boards and leave boards. For anything else, including deleting your account, write to prikboardinfo@gmail.com.

We process your data to provide the Service you asked for (performance of a contract) and to keep it secure and running (legitimate interest). You can also complain to your local data protection authority.

## 8. Children

The Service isn't meant for children under 16 (or the minimum age in your country), and Discord requires users to be at least 13.

## 9. Changes

We may update this policy. The date at the top shows the latest version. If a change is significant, we'll say so in the Service.

## 10. Contact

Questions or requests: **[CONTACT EMAIL]**.
