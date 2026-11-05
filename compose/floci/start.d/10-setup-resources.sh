#!/bin/bash

# S3 buckets
# aws s3 mb s3://my-bucket

# SNS topics
aws sns create-topic --name waste_regulator_case_management_analytics_events

# SQS queues
# aws sqs create-queue --queue-name my-queue
