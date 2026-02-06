# Solace PubSub+ Scenarios

This document summarizes the key messaging scenarios in this solution and what behavior to expect in the Publisher and Subscriber apps.

## Direct Topic (Pub/Sub)

Use when you want broadcast delivery to all matching subscribers.

- **Queue required**: No
- **Delivery pattern**: Publish/subscribe (fan-out)
- **Who receives**: Every active subscriber with a matching topic subscription
- **Persistence**: None (messages are not stored)
- **Typical use**: Live events, notifications, telemetry where all listeners should see the message

Notes:
- If two subscribers are connected to direct topic mode on the same topic, both will receive the message.

## Durable Queue (Load Balanced)

Use when you want one of multiple subscribers to receive each message.

- **Queue required**: Yes (pre-created in Solace)
- **Delivery pattern**: Competing consumers (load balanced)
- **Who receives**: Exactly one active consumer flow per message
- **Persistence**: Yes (queue spools messages)
- **Typical use**: Work distribution, background jobs, tasks that should be processed once

Notes:
- If the queue is **exclusive**, only one subscriber can connect at a time.
- If the queue is **non-exclusive**, multiple subscribers can connect and messages are distributed among them (each message delivered to only one).

## Partitioned Durable Queue (Keyed Ordering)

Use when you need ordering per key and horizontal scaling.

- **Queue required**: Yes (partitioned queue pre-created in Solace)
- **Delivery pattern**: Competing consumers with partition affinity
- **Who receives**: One consumer per partition; load balancing happens across partitions
- **Ordering**: Guaranteed **per key** (group key), not globally
- **Typical use**: Order processing, per-customer or per-entity workflows

Notes:
- Messages are assigned to partitions based on the **Group ID** (entity key).
- Consumers receive messages from specific partitions; distribution may look uneven if keys hash into the same partition.
- Changing the selected queue in the Subscriber UI requires **disconnect + reconnect**.

## UI Tips

- **Direct Topic mode**: both subscribers will receive each message if both are connected.
- **Durable Queue mode**: only one subscriber receives each message. If the queue is exclusive, only one consumer can connect at a time.
