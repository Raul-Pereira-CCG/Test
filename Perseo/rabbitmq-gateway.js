const express = require('express');
const amqp = require('amqplib');

const app = express();
app.use(express.json());

const RABBITMQ_URL = process.env.RABBITMQ_URL || 'amqp://guest:guest@57.128.119.16:5672';
const QUEUE = process.env.RABBITMQ_QUEUE || 'perseo_alerts';

let channel;

async function start() {
  const conn = await amqp.connect(RABBITMQ_URL);
  channel = await conn.createChannel();
  await channel.assertQueue(QUEUE);
}

app.post('/notify', async (req, res) => {
  try {
    const msg = JSON.stringify(req.body);
    console.log('Received from Perseo:', msg);
    await channel.sendToQueue(QUEUE, Buffer.from(msg));
    res.status(200).send('Sent to RabbitMQ');
  } catch (err) {
    console.error('Error:', err);
    res.status(500).send('Error forwarding to RabbitMQ');
  }
});

start().then(() => {
  const PORT = process.env.PORT || 9182;
  app.listen(PORT, () => {
    console.log(`Gateway running on port ${PORT}`);
  });
});
